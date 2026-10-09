using DesktopRelay.App;
using System.Diagnostics;
using System.Text.RegularExpressions;

void Assert(bool condition, string label) { if(!condition) throw new Exception(label); Console.WriteLine("PASS " + label); }
try {
Assert(Regex.IsMatch(@"app\ChatGPT.exe", DesktopLauncher.ExecutablePattern), "manifest backslash separator");
Assert(Regex.IsMatch("app/ChatGPT.exe", DesktopLauncher.ExecutablePattern), "manifest slash separator");
Assert(SystemProxy.Parse(true,"127.0.0.1:6134").Port == 6134, "single system proxy parsed");
Assert(SystemProxy.Parse(true,"http=127.0.0.1:7892;https=127.0.0.1:7892").Port == 7892, "shared HTTP/HTTPS proxy parsed");
foreach(var setting in new[]{(false,"127.0.0.1:6134"),(true,""),(true,"socks=127.0.0.1:1080"),(true,"http=127.0.0.1:6134;https=127.0.0.1:7892"),(true,"http://user:secret@localhost:7892")})
{
    bool rejected = false;
    try{SystemProxy.Parse(setting.Item1,setting.Item2);}catch(InvalidOperationException){rejected=true;}catch(ArgumentException){rejected=true;}
    Assert(rejected,"disabled/unsupported proxy rejected");
}
string? original = Environment.GetEnvironmentVariable("HTTP_PROXY");
Environment.SetEnvironmentVariable("HTTP_PROXY", "http://127.0.0.1:6134");
Environment.SetEnvironmentVariable("ALL_PROXY", "socks5://127.0.0.1:1080");
var info = DesktopLauncher.BuildStartInfo(@"C:\Apps\app\ChatGPT.exe", 18080);
Assert(info.Environment["HTTP_PROXY"] == "http://127.0.0.1:18080" && info.Environment["HTTPS_PROXY"] == "http://127.0.0.1:18080", "child uses stable entry, not inherited old port");
Assert(!info.Environment.ContainsKey("ALL_PROXY"), "child ALL_PROXY cleared");
Assert(info.Environment["NO_PROXY"] == "localhost,127.0.0.1,::1", "local bypass only");
Assert(Environment.GetEnvironmentVariable("HTTP_PROXY") == "http://127.0.0.1:6134", "parent environment not modified");
Assert(!info.UseShellExecute, "explicit child environment used");
Assert(info.ArgumentList.Contains("--proxy-server=http://127.0.0.1:18080"), "Chromium receives the stable proxy explicitly");
Assert(info.ArgumentList.Contains("--proxy-bypass-list=localhost;127.0.0.1;[::1]"), "Chromium loopback bypass explicit");
Assert(info.Environment.TryGetValue("NODE_USE_ENV_PROXY", out var nodeProxy) && nodeProxy == "1", "Node HTTP and WebSocket default agents enable environment proxy");
try { DesktopLauncher.BuildStartInfo("bad",0); throw new Exception("bad port accepted"); } catch(ArgumentOutOfRangeException) { Console.WriteLine("PASS invalid entry port rejected"); }
if (!args.Contains("--offline")) {
    string path = await DesktopLauncher.FindAsync();
    Assert(File.Exists(path) && Path.GetFileName(path) == "ChatGPT.exe", "current installed manifest executable located (read only)");
}
string logDirectory = Path.Combine(AppContext.BaseDirectory, "log-test"); Directory.CreateDirectory(logDirectory);
var persistentLog = new DiagnosticLog(logDirectory); persistentLog.Write("test-session-one");
new DiagnosticLog(logDirectory).Write("test-session-two\nno-new-record");
string savedLog = File.ReadAllText(persistentLog.PathName);
Assert(savedLog.Contains("test-session-one") && savedLog.Contains("test-session-two no-new-record"), "diagnostic survives logger recreation and neutralizes newlines");
File.WriteAllText(persistentLog.PathName, new string('x', 1024*1024)); persistentLog.Write("rotation-new-record");
Assert(File.ReadAllText(persistentLog.PathName).Contains("rotation-new-record") && File.Exists(persistentLog.PathName+".previous"), "diagnostic rotation preserves previous log and bounds current file");
await NodeProxyRegression.RunAsync();
Environment.SetEnvironmentVariable("HTTP_PROXY", original);
Console.WriteLine("ALL WINDOWS TESTS PASS; no Desktop process launched or stopped");
} catch(Exception error) { Console.Error.WriteLine(error); Environment.ExitCode=1; }
