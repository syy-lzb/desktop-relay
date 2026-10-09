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
        ModeBox.SelectedIndex = Math.Clamp(settings.Mode, 0, 2);
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
        try { ApplySelected(); }
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
            0 => SystemProxy.Read(),
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
            if (ModeBox.SelectedIndex == 0) { var route = SystemProxy.Read(); AddressBox.Text = Describe(route); RouteNote.Text = "读取 Windows 当前地址；变化后手动应用。"; }
            else if (ModeBox.SelectedIndex == 1) { AddressBox.Text = settings.CustomProxy; RouteNote.Text = "输入 HTTP / 混合代理入口，不填 VLESS 服务器。"; }
            else { AddressBox.Text = "由系统网络接管"; RouteNote.Text = "不会自动检测或启用 TUN。"; }
        }
        catch (Exception ex) { AddressBox.Text = "未检测到静态代理"; RouteNote.Text = ex.Message; }
        changed = true;
        ApplyButton.Content = "应用到新连接";
        ApplyButton.IsEnabled = !preview && entryReady;
    }
    private void ModeChanged(object sender, SelectionChangedEventArgs e) => RefreshSelection();
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
        MiniStatus.Text = route.Mode == RouteMode.Direct ? "直连 / TUN · 未检测" : (ModeBox.SelectedIndex == 0 ? "系统代理 · 未检测" : "自定义代理 · 未检测");
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
        if (ModeBox.SelectedIndex != 0) return;
        string next = SystemProxy.Fingerprint();
        if (next != systemFingerprint)
        {
            systemFingerprint = next; RefreshSelection();
            RouteNote.Text = "系统代理已变化。当前出口保持原值，请点击应用。";
            Log("系统代理变化，等待手动应用。");
        }
    }
    private async void CheckClick(object sender, RoutedEventArgs e)
    {
        if (preview || busy) return;
        busy = true; CheckButton.IsEnabled = false;
        StatusText.Text = "正在检查 Relay 转发链路…";
        try
        {
            if (!entryReady || !applied || changed) throw new InvalidOperationException("请先应用出口，再检查实际转发链路。");
            using var handler = new HttpClientHandler { UseProxy = true, AllowAutoRedirect = false, Proxy = new WebProxy($"http://127.0.0.1:{relay.Port}") };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            using var request = new HttpRequestMessage(HttpMethod.Head, "https://chatgpt.com/") { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            StatusText.Text = $"Relay 链路收到 HTTP {(int)response.StatusCode}";
            RouteNote.Text = "已通过固定入口验证 HTTPS 转发；账号与 Desktop WebSocket 尚需实际请求验证。";
            Log("HTTPS end-to-end probe via relay received HTTP " + (int)response.StatusCode);
        }
        catch (Exception ex) { StatusText.Text = "Relay 链路检测失败"; RouteNote.Text = "请先应用出口，并检查诊断记录中的失败阶段。"; Log("HTTPS end-to-end probe failed: " + ex.GetType().Name); }
        finally { busy = false; CheckButton.IsEnabled = true; UpdateProcessState(); }
    }
    private void SetView(string mode)
    {
        view = mode is "capsule" or "pet" ? "capsule" : "expanded";
        Card.Visibility = view == "expanded" ? Visibility.Visible : Visibility.Collapsed;
        Capsule.Visibility = view == "capsule" ? Visibility.Visible : Visibility.Collapsed;
        Width = view == "expanded" ? 304 : 250;
        Height = view == "expanded" ? 414 : 78;
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
        if (e.LeftButton == MouseButtonState.Pressed) { DragMove(); KeepVisible(); Save(); }
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
