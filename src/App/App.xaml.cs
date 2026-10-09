using System;
using System.Linq;
using System.IO;
using System.Threading;
using System.Windows;

namespace DesktopRelay.App;
public partial class App : Application
{
    private Mutex? mutex;
    protected override void OnStartup(StartupEventArgs e)
    {
        Trace("OnStartup " + string.Join(" ", e.Args));
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        base.OnStartup(e);
        bool preview = e.Args.Contains("--preview") || e.Args.Contains("--render-preview");
        if (!preview)
        {
            mutex = new Mutex(true, "Local\\DesktopRelay.DragonGirl", out bool first);
            if (!first) { MessageBox.Show("挂件已在运行，请点击托盘中的龙娘图标。", "Desktop Relay"); Shutdown(); return; }
        }
        Trace("Creating window");
        var window = new MainWindow(preview);
        if (preview) window.ShowInTaskbar = true;
        Trace("Window constructed");
        MainWindow = window;
        window.Show();
        Trace("Window shown");
    }
    internal static void Trace(string text)
    {
        if (!Environment.GetCommandLineArgs().Contains("--preview")) return;
        File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "preview-startup.log"), text + Environment.NewLine);
    }
    protected override void OnExit(ExitEventArgs e) { mutex?.Dispose(); base.OnExit(e); }
}
