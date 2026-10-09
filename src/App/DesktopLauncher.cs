using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;

namespace DesktopRelay.App;
public static class DesktopLauncher
{
    public const string ExecutablePattern = @"(^|[\\/])ChatGPT\.exe$";
    public static bool IsRunning() => Process.GetProcessesByName("ChatGPT").Any(p => { using(p) return true; });
    public static async Task<string> FindAsync()
    {
        string command = "$ErrorActionPreference='Stop'; $p=@(Get-AppxPackage -Name OpenAI.Codex); if($p.Count -ne 1){throw 'Cannot uniquely locate OpenAI.Codex'}; [xml]$m=Get-Content -LiteralPath (Join-Path $p[0].InstallLocation 'AppxManifest.xml'); $a=@($m.Package.Applications.Application | Where-Object { $_.Executable -match '" + ExecutablePattern + "' }); if($a.Count -ne 1){throw 'Cannot locate ChatGPT executable'}; @{root=$p[0].InstallLocation; executable=$a[0].Executable} | ConvertTo-Json -Compress";
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"System32\WindowsPowerShell\v1.0\powershell.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("-NoProfile"); info.ArgumentList.Add("-NonInteractive"); info.ArgumentList.Add("-Command"); info.ArgumentList.Add(command);
        using var p = Process.Start(info) ?? throw new InvalidOperationException("无法查询安装信息。");
        Task<string> outputTask = p.StandardOutput.ReadToEndAsync(); Task<string> errorTask = p.StandardError.ReadToEndAsync();
        try { await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25)); }
        catch (TimeoutException) { try { p.Kill(); } catch {} throw new InvalidOperationException("读取安装位置超时，未关闭 Desktop。"); }
        var output = await outputTask; var error = await errorTask;
        if (p.ExitCode != 0) throw new InvalidOperationException("未找到当前用户的 OpenAI.Codex 安装包。" + (string.IsNullOrWhiteSpace(error) ? "" : "请确认应用已安装。"));
        using var json = JsonDocument.Parse(output.Trim());
        string root = json.RootElement.GetProperty("root").GetString() ?? "";
        string relative = json.RootElement.GetProperty("executable").GetString() ?? "";
        string path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new InvalidOperationException("安装清单中的执行文件路径无效。");
        return path;
    }
    public static ProcessStartInfo BuildStartInfo(string executable, int entryPort)
    {
        if (entryPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(entryPort));
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory };
        foreach (string key in info.Environment.Keys.Where(k => k.Equals("HTTP_PROXY", StringComparison.OrdinalIgnoreCase) || k.Equals("HTTPS_PROXY", StringComparison.OrdinalIgnoreCase) || k.Equals("ALL_PROXY", StringComparison.OrdinalIgnoreCase) || k.Equals("NO_PROXY", StringComparison.OrdinalIgnoreCase)).ToArray()) info.Environment.Remove(key);
        info.Environment["HTTP_PROXY"] = $"http://127.0.0.1:{entryPort}";
        info.Environment["HTTPS_PROXY"] = $"http://127.0.0.1:{entryPort}";
        info.Environment["NO_PROXY"] = "localhost,127.0.0.1,::1";
        info.Environment["NODE_USE_ENV_PROXY"] = "1";
        info.ArgumentList.Add($"--proxy-server=http://127.0.0.1:{entryPort}");
        info.ArgumentList.Add("--proxy-bypass-list=localhost;127.0.0.1;[::1]");
        return info;
    }
    public static async Task LaunchAsync(int entryPort)
    {
        if (IsRunning()) throw new InvalidOperationException("Desktop 已在运行，请使用“显示 Desktop”。首次接入需要任务完成后完整退出，再由挂件启动。");
        string path = await FindAsync();
        if (IsRunning()) throw new InvalidOperationException("Desktop 已启动，本次不重复启动。");
        using var p = Process.Start(BuildStartInfo(path, entryPort)) ?? throw new InvalidOperationException("Desktop 启动失败。");
    }
    public static bool ShowExisting()
    {
        var ids = Process.GetProcessesByName("ChatGPT").Select(p => { using (p) return (uint)p.Id; }).ToHashSet();
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) => { GetWindowThreadProcessId(h, out uint id); if (ids.Contains(id) && IsWindowVisible(h) && GetWindowTextLength(h) > 0) { found = h; return false; } return true; }, IntPtr.Zero);
        if (found == IntPtr.Zero) return false;
        ShowWindow(found, 9); return SetForegroundWindow(found);
    }
    private delegate bool EnumCallback(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, IntPtr p);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
}
