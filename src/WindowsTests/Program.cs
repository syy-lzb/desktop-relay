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
var candidates = LocalProxyDiscovery.Parse("  TCP    127.0.0.1:7892    0.0.0.0:0    LISTENING    2072\n  TCP    0.0.0.0:6134    0.0.0.0:0    LISTENING    42\n  TCP    127.0.0.1:58466    0.0.0.0:0    LISTENING    2072\n  TCP    127.0.0.1:9000    0.0.0.0:0    LISTENING    1\n  TCP    127.0.0.1:7892    1.2.3.4:443    ESTABLISHED    2072", id => id == 2072 ? "FlyingBirdCore" : id == 42 ? "CloudFox" : "other", 58466);
Assert(candidates.Count == 2 && candidates.Any(c => c.Address == "http://127.0.0.1:7892"), "discovery identifies proxy listeners and excludes relay, unrelated processes and established connections");
var anchor = new CapsuleAnchor();
anchor.Remember(1700, 950);
var restored = anchor.Restore(1500, 550);
Assert(restored == (1700d, 950d), "expansion clamping does not overwrite capsule anchor");
anchor.Remember(100, 200);
Assert(anchor.Restore(0, 0) == (100d,200d), "dragged capsule updates its return position");
var attempted = new List<string>();
var autoResult = await AutoProxy.ResolveAsync("http://127.0.0.1:6134", () => Task.FromResult(new List<LocalProxyCandidate>{new("http://127.0.0.1:7892", "FlyingBirdCore")}), address => { attempted.Add(address); return Task.FromResult(true); });
Assert(autoResult.Count == 1 && attempted.SequenceEqual(new[]{"http://127.0.0.1:6134"}), "usable system proxy takes priority over local discovery");
attempted.Clear();
autoResult = await AutoProxy.ResolveAsync("http://127.0.0.1:6134", () => Task.FromResult(new List<LocalProxyCandidate>{new("http://127.0.0.1:7892", "FlyingBirdCore"),new("http://127.0.0.1:10808", "xray")}), address => { attempted.Add(address); return Task.FromResult(address.EndsWith(":7892")); });
Assert(autoResult.Count == 1 && autoResult[0].Address.EndsWith(":7892"), "failed system and non HTTP listeners fall back to validated local proxy");
autoResult = await AutoProxy.ResolveAsync(null, () => Task.FromResult(new List<LocalProxyCandidate>{new("http://127.0.0.1:7892", "FlyingBirdCore"),new("http://127.0.0.1:6134", "CloudFox")}), _ => Task.FromResult(true));
Assert(autoResult.Count == 2, "multiple usable proxies preserved for user selection");
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
