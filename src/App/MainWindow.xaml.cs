using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopRelay.Core;
using Forms = System.Windows.Forms;

namespace DesktopRelay.App;
public partial class MainWindow : Window
{
    private readonly bool preview;
    private readonly Settings settings;
    private readonly string? settingsError;
    private readonly RelayServer relay;
    private readonly DiagnosticLog diagnostics = new(AppContext.BaseDirectory);
    private bool diagnosticsFailed;
    private readonly Queue<string> messages = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private Forms.NotifyIcon? tray;
    private Icon? trayIcon;
    private bool initialized, exiting, applied, changed, launched, busy, entryReady;
    private readonly CapsuleAnchor capsuleAnchor = new();
    private Route? automaticRoute;
    private bool detecting;
    private string systemFingerprint = "";
    private string view = "expanded";
    public MainWindow(bool preview)
    {
        this.preview = preview;
        try { settings = preview ? new Settings() : Settings.Load(); }
        catch (Exception ex) { settings = new Settings(); settingsError = ex.Message; }
        relay = new RelayServer(Log);
        App.Trace("Before XAML");
        InitializeComponent();
        App.Trace("After XAML");
        ModeBox.SelectedIndex = 0;
        AddressBox.Text = settings.CustomProxy;
        initialized = true;
        Topmost = settings.Topmost;
        Left = double.IsFinite(settings.Left) ? settings.Left : 80;
        Top = double.IsFinite(settings.Top) ? settings.Top : 100;
        LoadCharacter();
        App.Trace("Image loaded");
        RefreshSelection();
        App.Trace("Selection refreshed");
        if (preview)
        {
            StatusText.Text = "概念预览 · 未启动入口或 Desktop";
            ApplyButton.IsEnabled = LaunchButton.IsEnabled = CheckButton.IsEnabled = false;
            SetView("expanded");
        }
        else SetView(settings.View);
        Loaded += async (_, _) => await InitializeAsync();
        Closing += OnClosing;
        LocationChanged += (_, _) => { if (IsLoaded && !preview) { settings.Left = Left; settings.Top = Top; } };
    }
    private void LoadCharacter()
    {
        var image = new BitmapImage(new Uri("pack://application:,,,/Assets/dragon.png"));
        // Face-region clipping remains a UI presentation of the bundled artwork, not a new edited asset.
        var crop = new CroppedBitmap(image, new Int32Rect((int)(image.PixelWidth * .17), 0, (int)(image.PixelWidth * .65), (int)(image.PixelHeight * .55)));
        HeaderAvatar.Source = CapsuleAvatar.Source = crop;
    }
    private async Task InitializeAsync()
    {
        KeepVisible();
        if (preview)
        {
            App.Trace($"Loaded visible={IsVisible} at={Left},{Top} size={ActualWidth},{ActualHeight}");
            if (Environment.GetCommandLineArgs().Contains("--render-preview"))
            {
                await Dispatcher.InvokeAsync(() => {}, DispatcherPriority.Render);
                ExportPreview("preview-expanded.png");
                SetView("capsule");
                MiniStatus.Text = "系统代理 · 未启动";
                await Dispatcher.InvokeAsync(() => {}, DispatcherPriority.Render);
                ExportPreview("preview-capsule.png");
                exiting = true; System.Windows.Application.Current.Shutdown();
            }
            return;
        }
        SetupTray();
        Log("Relay revision 2 startup; explicit Chromium proxy and Node environment proxy on child launch");
        if (settingsError != null) { StatusText.Text = "设置读取失败 · 入口未启动"; RouteNote.Text = settingsError; Log(settingsError); return; }
        try
        {
            if (settings.EntryPort is < 0 or > 65535) throw new InvalidOperationException("保存的入口端口无效，请在 settings.json 中将 EntryPort 设为 0 后重新打开挂件。");
            relay.Start(settings.EntryPort);
            settings.EntryPort = relay.Port;
            settings.Save();
            entryReady = true;
        }
        catch (Exception ex) { StatusText.Text = "入口未就绪"; Log(ex.Message); RouteNote.Text = ex.Message; await relay.StopAsync(); return; }
        systemFingerprint = SystemProxy.Fingerprint();
        // Preserve the last explicit route while isolated probes run; probes never change live forwarding.
        try
        {
            if (settings.Mode == 1) { relay.SwitchRoute(Route.ParseProxy(settings.CustomProxy)); applied = true; }
            else if (settings.Mode == 2) { relay.SwitchRoute(new Route(RouteMode.Direct)); applied = true; }
        }
        catch (Exception ex) { Log("Saved route unavailable: " + ex.Message); }
        try { await DetectAutomaticAsync(true); }
        catch (Exception ex) { StatusText.Text = "入口就绪 · 等待选择出口"; MiniStatus.Text = "待选择出口"; Log(ex.Message); RouteNote.Text = ex.Message; }
        timer.Tick += (_, _) => Tick(); timer.Start();
        await Task.CompletedTask;
        UpdateProcessState();
    }
    private void SetupTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示连接助手", null, (_, _) => Dispatcher.Invoke(() => { SetView("expanded"); Show(); Activate(); }));
        menu.Items.Add("胶囊模式", null, (_, _) => Dispatcher.Invoke(() => { SetView("capsule"); Show(); }));
        var top = new Forms.ToolStripMenuItem("始终置顶") { Checked = Topmost, CheckOnClick = true };
        top.CheckedChanged += (_, _) => Dispatcher.Invoke(() => { Topmost = top.Checked; settings.Topmost = Topmost; Save(); });
        menu.Items.Add(top); menu.Items.Add("诊断 / 设置", null, (_, _) => Dispatcher.Invoke(ShowDetails));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("停止转发并退出", null, async (_, _) => await Dispatcher.InvokeAsync(ExitAsync).Task.Unwrap());
        using (var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/dragon.png"))!.Stream)
        using (var original = new Bitmap(resource))
        using (var thumb = new Bitmap(32, 32))
        {
            using (var graphics = Graphics.FromImage(thumb))
            {
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(original, new System.Drawing.Rectangle(0, 0, 32, 32), new System.Drawing.RectangleF(original.Width * .17f, 0, original.Width * .65f, original.Height * .55f), GraphicsUnit.Pixel);
            }
            IntPtr nativeIcon = thumb.GetHicon();
            try { using var borrowed = System.Drawing.Icon.FromHandle(nativeIcon); trayIcon = (Icon)borrowed.Clone(); }
            finally { DestroyIcon(nativeIcon); }
        }
        tray = new Forms.NotifyIcon { Icon = trayIcon, Text = "Desktop Relay", Visible = true, ContextMenuStrip = menu };
        tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) Dispatcher.Invoke(() => { Show(); Activate(); }); };
    }
    private void Log(string message)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => Log(message)); return; }
        messages.Enqueue($"{DateTime.Now:HH:mm:ss}  {message}");
        while (messages.Count > 200) messages.Dequeue();
        if (!preview)
        {
            try { diagnostics.Write(message); }
            catch (Exception ex)
            {
                if (!diagnosticsFailed) { messages.Enqueue("诊断日志写入失败：" + ex.GetType().Name); diagnosticsFailed = true; }
            }
        }
    }
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);
    private void Save()
    {
        if (preview || settingsError != null) return;
        try { settings.Save(); } catch (Exception ex) { Log("设置保存失败：" + ex.Message); }
    }
    private Route SelectedRoute()
    {
        return ModeBox.SelectedIndex switch
        {
            0 => automaticRoute ?? throw new InvalidOperationException("自动检测尚未找到可用代理。"),
            1 => Route.ParseProxy(AddressBox.Text.Trim()),
            2 => new Route(RouteMode.Direct),
            _ => throw new InvalidOperationException("请选择连接方式。")
        };
    }
    private static string Describe(Route route) => route.Mode == RouteMode.Direct ? "直连 / TUN" : $"{route.Host}:{route.Port}";
    private void RefreshSelection()
    {
        if (!initialized) return;
        AddressBox.IsReadOnly = ModeBox.SelectedIndex != 1;
        try
        {
            if (ModeBox.SelectedIndex == 0) { AddressBox.Text = automaticRoute == null ? "等待自动检测" : Describe(automaticRoute); RouteNote.Text = "优先系统代理，再验证本地代理。"; }
            else if (ModeBox.SelectedIndex == 1) { AddressBox.Text = settings.CustomProxy; RouteNote.Text = "输入 HTTP / 混合代理入口，不填 VLESS 服务器。"; }
            else { AddressBox.Text = "由系统网络接管"; RouteNote.Text = "不会自动检测或启用 TUN。"; }
        }
        catch (Exception ex) { AddressBox.Text = "系统静态代理不可用"; RouteNote.Text = ex.Message + " 可点击发现本地代理。"; }
        changed = true;
        ApplyButton.Content = "应用到新连接";
        ApplyButton.IsEnabled = !preview && entryReady;
    }
    private async void ModeChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshSelection();
        if (initialized && entryReady && !detecting && ModeBox.SelectedIndex == 0) await DetectAutomaticAsync(false);
    }
    private void AddressChanged(object sender, TextChangedEventArgs e)
    {
        if (!initialized || ModeBox.SelectedIndex != 1) return;
        settings.CustomProxy = AddressBox.Text;
        changed = true;
        ApplyButton.Content = "应用到新连接";
        ApplyButton.IsEnabled = !preview && entryReady;
    }
    private void ApplySelected()
    {
        if (!entryReady) throw new InvalidOperationException("入口未就绪，无法应用出口。");
        var route = SelectedRoute();
        relay.SwitchRoute(route);
        applied = true; changed = false;
        settings.Mode = ModeBox.SelectedIndex;
        if (settings.Mode == 1) settings.CustomProxy = AddressBox.Text.Trim();
        systemFingerprint = SystemProxy.Fingerprint();
        StatusText.Text = "入口就绪 · 出口未检测";
        MiniStatus.Text = route.Mode == RouteMode.Direct ? "直连 / TUN · 未检测" : (ModeBox.SelectedIndex == 0 ? "自动代理 · 未检测" : "自定义代理 · 未检测");
        MiniStatus.ToolTip = Describe(route);
        RouteNote.Text = route.Mode == RouteMode.Direct ? "直接连接，由系统网络决定路由。" : "已应用，后续新连接使用此地址。";
        ApplyButton.Content = "已应用"; ApplyButton.IsEnabled = false;
        Log("新连接出口：" + Describe(route)); Save(); UpdateProcessState();
    }
    private void ApplyClick(object sender, RoutedEventArgs e)
    {
        try { ApplySelected(); }
        catch (Exception ex) { RouteNote.Text = ex.Message; Log("应用失败：" + ex.Message); }
    }
    private async void LaunchClick(object sender, RoutedEventArgs e)
    {
        if (preview || busy) return;
        if (DesktopLauncher.IsRunning())
        {
            if (!DesktopLauncher.ShowExisting()) RouteNote.Text = "已有进程，但未能显示窗口。请从任务栏打开，不会关闭任何任务。";
            return;
        }
        if (!applied || changed) { RouteNote.Text = "请先应用选定的出口，再启动 Desktop。"; return; }
        busy = true; LaunchButton.IsEnabled = false;
        try { await DesktopLauncher.LaunchAsync(relay.Port); launched = true; Log("已请求启动 Desktop；尚未验证所有组件均经过入口。"); RouteNote.Text = "已请求启动。请保持挂件运行。"; }
        catch (Exception ex) { RouteNote.Text = ex.Message; Log("启动失败：" + ex.Message); }
        finally { busy = false; UpdateProcessState(); }
    }
    private void UpdateProcessState()
    {
        if (preview) return;
        bool running = DesktopLauncher.IsRunning();
        if (!running) launched = false;
        LaunchButton.Content = running ? "显示 Desktop" : "启动 Desktop";
        LaunchButton.ToolTip = running ? (launched ? "由本次挂件请求启动，实际接入仍需日志验证" : "已运行，未确认接入。首次接入需完整退出再通过挂件启动") : "使用固定本地入口启动当前安装版本";
        LaunchButton.IsEnabled = !busy && (running || (entryReady && applied && !changed));
    }
    private void Tick()
    {
        UpdateProcessState();
        if (busy || ModeBox.SelectedIndex != 0) return;
        string next = SystemProxy.Fingerprint();
        if (next != systemFingerprint)
        {
            systemFingerprint = next; automaticRoute = null; RefreshSelection();
            _ = DetectAutomaticAsync(false);
            RouteNote.Text = "系统代理已变化。当前出口保持原值，请点击应用。";
            Log("系统代理变化，等待手动应用。");
        }
    }
    private async Task<bool> ProbeCandidateAsync(string address)
    {
        try
        {
            using var handler = new HttpClientHandler { UseProxy = true, Proxy = new WebProxy(address), AllowAutoRedirect = false };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
            using var request = new HttpRequestMessage(HttpMethod.Head, "https://chatgpt.com/") { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            Log($"Automatic probe address={address} HTTP={(int)response.StatusCode}");
            // HTTP 403 still proves a completed HTTPS exchange, not account or WebSocket availability.
            return (int)response.StatusCode < 500;
        }
        catch (Exception ex)
        {
            for (Exception? cause = ex; cause != null; cause = cause.InnerException)
                Log($"Automatic probe address={address} failed={cause.GetType().Name}: {cause.Message}");
            return false;
        }
    }
    private async Task DetectAutomaticAsync(bool applySingle)
    {
        if (preview || busy || detecting || !entryReady) return;
        detecting = busy = true;
        ModeBox.IsEnabled = AddressBox.IsEnabled = ApplyButton.IsEnabled = CheckButton.IsEnabled = DiscoverButton.IsEnabled = false;
        StatusText.Text = "正在自动发现并验证代理…";
        try
        {
            string? system = null;
            try { var route = SystemProxy.Read(); if (!(route.Port == relay.Port && (route.Host == "127.0.0.1" || route.Host == "localhost"))) system = $"http://{route.Host}:{route.Port}"; }
            catch (Exception ex) { Log("Automatic system proxy unavailable: " + ex.Message); }
            var candidates = await AutoProxy.ResolveAsync(system, () => LocalProxyDiscovery.ReadAsync(relay.Port), ProbeCandidateAsync);
            if (candidates.Count == 1)
            {
                automaticRoute = Route.ParseProxy(candidates[0].Address);
                ModeBox.SelectedIndex = 0; RefreshSelection();
                if (applySingle) ApplySelected();
                StatusText.Text = applySingle ? "自动代理已应用 · HTTPS 有响应" : "已发现可用代理 · 等待应用";
                RouteNote.Text = "HTTPS 验证有响应；账号与 WebSocket 仍需实际使用确认。";
            }
            else if (candidates.Count == 0)
            {
                automaticRoute = null;
                ModeBox.SelectedIndex = 1; RefreshSelection();
                StatusText.Text = "自动检测未找到可用代理";
                RouteNote.Text = "请填写自定义 HTTP 代理，或手动选择直连 / TUN。当前已应用的出口保持不变。";
            }
            else
            {
                StatusText.Text = "发现多个有 HTTPS 响应的代理";
                var panel = new StackPanel { Margin = new Thickness(16) };
                panel.Children.Add(new TextBlock { Text = "请选择出口；选择后点击应用到新连接。", TextWrapping = TextWrapping.Wrap });
                var window = new Window { Title = "选择自动发现的代理", Width = 460, Height = 320, Owner = this, Content = new ScrollViewer { Content = panel }, WindowStartupLocation = WindowStartupLocation.CenterOwner };
                foreach (var candidate in candidates)
                {
                    var button = new Button { Content = candidate.ProcessName + " · " + candidate.Address, Margin = new Thickness(0,4,0,4) };
                    button.Click += (_, _) => { automaticRoute = Route.ParseProxy(candidate.Address); ModeBox.SelectedIndex = 0; RefreshSelection(); window.Close(); };
                    panel.Children.Add(button);
                }
                window.Show();
            }
        }
        catch (Exception ex) { StatusText.Text = "自动发现失败"; RouteNote.Text = ex.Message; Log("Automatic discovery failed: " + ex.Message); }
        finally
        {
            detecting = busy = false;
            ModeBox.IsEnabled = AddressBox.IsEnabled = CheckButton.IsEnabled = DiscoverButton.IsEnabled = true;
            ApplyButton.IsEnabled = changed; UpdateProcessState();
        }
    }
    private async void DiscoverClick(object sender, RoutedEventArgs e) => await DetectAutomaticAsync(false);
    private async void CheckClick(object sender, RoutedEventArgs e)
    {
        if (preview || busy) return;
        if (!entryReady) { StatusText.Text = "入口尚未就绪"; RouteNote.Text = "没有发起网络检测，请查看入口启动记录。"; Log("Probe skipped: entry not ready"); return; }
        if (!applied || changed) { StatusText.Text = "所选出口尚未应用"; RouteNote.Text = "请点击应用到新连接，再检查链路；本次未发起网络请求。"; Log("Probe skipped: selected route not applied"); return; }
        busy = true; CheckButton.IsEnabled = ApplyButton.IsEnabled = ModeBox.IsEnabled = AddressBox.IsEnabled = DiscoverButton.IsEnabled = false;
        StatusText.Text = "正在检查 Relay 转发链路…";
        try
        {
            using var handler = new HttpClientHandler { UseProxy = true, AllowAutoRedirect = false, Proxy = new WebProxy($"http://127.0.0.1:{relay.Port}") };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            using var request = new HttpRequestMessage(HttpMethod.Head, "https://chatgpt.com/") { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            StatusText.Text = $"Relay 链路收到 HTTP {(int)response.StatusCode}";
            RouteNote.Text = "已通过固定入口验证 HTTPS 转发；账号与 Desktop WebSocket 尚需实际请求验证。";
            Log("HTTPS end-to-end probe via relay received HTTP " + (int)response.StatusCode);
        }
        catch (Exception ex)
        {
            StatusText.Text = ex is OperationCanceledException ? "HTTPS 检测超时（15 秒）" : "HTTPS 链路检测失败";
            RouteNote.Text = ex is OperationCanceledException ? "已应用出口，但 15 秒内未收到响应；详见诊断记录。" : ex.Message;
            for (Exception? cause = ex; cause != null; cause = cause.InnerException)
                Log("HTTPS end-to-end probe failed: " + cause.GetType().Name + " HResult=" + cause.HResult + ": " + cause.Message);
        }
        finally { busy = false; CheckButton.IsEnabled = ModeBox.IsEnabled = AddressBox.IsEnabled = DiscoverButton.IsEnabled = true; ApplyButton.IsEnabled = changed; UpdateProcessState(); }
    }
    private void SetView(string mode)
    {
        string nextView = mode is "capsule" or "pet" ? "capsule" : "expanded";
        if (IsLoaded && view == "capsule" && nextView == "expanded") capsuleAnchor.Remember(Left, Top);
        bool restoreCapsule = IsLoaded && view == "expanded" && nextView == "capsule";
        view = nextView;
        Card.Visibility = view == "expanded" ? Visibility.Visible : Visibility.Collapsed;
        Capsule.Visibility = view == "capsule" ? Visibility.Visible : Visibility.Collapsed;
        Width = view == "expanded" ? 304 : 250;
        Height = view == "expanded" ? 454 : 78;
        if (restoreCapsule) { var anchor = capsuleAnchor.Restore(Left, Top); Left = anchor.Left; Top = anchor.Top; }
        settings.View = view;
        if (IsLoaded) { KeepVisible(); Save(); }
    }
    private void KeepVisible()
    {
        // Work in device pixels when choosing a real monitor, then convert its work area to WPF DIPs.
        var source = PresentationSource.FromVisual(this);
        var matrix = source?.CompositionTarget?.TransformToDevice ?? System.Windows.Media.Matrix.Identity;
        var bounds = new System.Drawing.Rectangle((int)(Left * matrix.M11), (int)(Top * matrix.M22), Math.Max(1,(int)(Width * matrix.M11)), Math.Max(1,(int)(Height * matrix.M22)));
        var screen = Forms.Screen.AllScreens.OrderByDescending(s => System.Drawing.Rectangle.Intersect(bounds, s.WorkingArea).Width * System.Drawing.Rectangle.Intersect(bounds, s.WorkingArea).Height).First();
        var area = screen.WorkingArea;
        Left = Math.Clamp(Left, area.Left / matrix.M11, Math.Max(area.Left / matrix.M11, area.Right / matrix.M11 - Width));
        Top = Math.Clamp(Top, area.Top / matrix.M22, Math.Max(area.Top / matrix.M22, area.Bottom / matrix.M22 - Height));
    }
    private void ExpandClick(object sender, RoutedEventArgs e) => SetView("expanded");
    private void ExportPreview(string fileName)
    {
        UpdateLayout();
        var image = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(ActualWidth), (int)Math.Ceiling(ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        image.Render(this);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
        using var stream = File.Create(Path.Combine(AppContext.BaseDirectory, fileName)); encoder.Save(stream);
    }
    private void CollapseClick(object sender, RoutedEventArgs e) => SetView("capsule");
    private void DragHeader(object sender, MouseButtonEventArgs e)
    {
        var element = e.OriginalSource as DependencyObject;
        while (element != null) { if (element is System.Windows.Controls.Primitives.ButtonBase || element is TextBox || element is ComboBox) return; element = System.Windows.Media.VisualTreeHelper.GetParent(element); }
        if (e.LeftButton == MouseButtonState.Pressed) { DragMove(); KeepVisible(); if (view == "capsule") capsuleAnchor.Remember(Left, Top); Save(); }
    }
    private void DetailsClick(object sender, RoutedEventArgs e) => ShowDetails();
    private void ShowDetails()
    {
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "修正版 2 · 日志保存在 relay-diagnostics.log", FontSize = 11, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = "诊断与设置", FontSize = 20 });
        panel.Children.Add(new TextBlock { Text = $"固定入口：127.0.0.1:{relay.Port}\n活动连接：{relay.ActiveConnections}\n当前出口：{(applied ? Describe(relay.CurrentRoute) : "未应用")}\nDesktop：{(DesktopLauncher.IsRunning() ? (launched ? "本次请求启动，待核实接入" : "已运行，未确认接入") : "未运行")}", Margin = new Thickness(0, 12, 0, 12) });
        var top = new CheckBox { Content = "始终置顶", IsChecked = Topmost, Margin = new Thickness(0, 0, 0, 10) };
        top.Click += (_, _) => { Topmost = top.IsChecked == true; settings.Topmost = Topmost; Save(); }; panel.Children.Add(top);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var capsuleButton = new Button { Content = "胶囊", Margin = new Thickness(6, 3, 6, 3) }; capsuleButton.Click += (_, _) => SetView("capsule"); row.Children.Add(capsuleButton);
        var hideButton = new Button { Content = "隐藏到托盘" }; hideButton.Click += (_, _) => { if (!preview) Hide(); }; row.Children.Add(hideButton); panel.Children.Add(row);
        var logs = new TextBox { Text = string.Join(Environment.NewLine, messages.ToArray()), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 220, Margin = new Thickness(0, 12, 0, 8) }; panel.Children.Add(logs);
        var exit = new Button { Content = preview ? "关闭预览" : "停止转发并退出" }; exit.Click += async (_, _) => await ExitAsync(); panel.Children.Add(exit);
        var window = new Window { Title = "Desktop Relay · 诊断", Width = 450, Height = 560, Content = panel, Owner = this, Background = System.Windows.Media.Brushes.FloralWhite, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        window.Show();
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (exiting) return;
        e.Cancel = true;
        if (preview) { _ = ExitAsync(); return; }
        Hide(); Save();
    }
    private async Task ExitAsync()
    {
        if (exiting) return;
        if (!preview && (relay.ActiveConnections > 0 || DesktopLauncher.IsRunning()))
        {
            if (MessageBox.Show("停止转发后，经过挂件入口的连接将不可用。Desktop 不会被关闭。确定退出？", "停止转发", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        }
        exiting = true; timer.Stop(); Save();
        tray?.Dispose(); trayIcon?.Dispose();
        await relay.StopAsync();
        System.Windows.Application.Current.Shutdown();
    }
}
