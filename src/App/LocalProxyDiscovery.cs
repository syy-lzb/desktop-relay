using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DesktopRelay.App;
public sealed record LocalProxyCandidate(string Address, string ProcessName)
{
    public override string ToString() => $"{ProcessName} · {Address}（协议待验证）";
}
public static class LocalProxyDiscovery
{
    public static List<LocalProxyCandidate> Parse(string output, Func<int, string?> processName, int entryPort)
    {
        var result = new List<LocalProxyCandidate>();
        foreach (var line in output.Split('\n'))
        {
            var match = Regex.Match(line, @"^\s*TCP\s+(127\.0\.0\.1|0\.0\.0\.0):(\d+)\s+\S+\s+LISTENING\s+(\d+)\s*$", RegexOptions.IgnoreCase);
            if (!match.Success || !int.TryParse(match.Groups[2].Value, out int port) || port == entryPort) continue;
            var name = processName(int.Parse(match.Groups[3].Value)) ?? "";
            if (!Regex.IsMatch(name, "flyingbird|cloud.?fox|v2ray|xray|sing.?box|clash|mihomo", RegexOptions.IgnoreCase)) continue;
            string address = $"http://127.0.0.1:{port}";
            if (!result.Any(c => c.Address == address)) result.Add(new(address, name));
        }
        return result.OrderBy(c => c.Address).ToList();
    }
    public static async Task<List<LocalProxyCandidate>> ReadAsync(int entryPort)
    {
        using var process = Process.Start(new ProcessStartInfo(System.IO.Path.Combine(Environment.SystemDirectory, "netstat.exe"), "-ano -p tcp")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true }) ?? throw new InvalidOperationException("无法读取本地监听端口。");
        string output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException("读取本地监听端口失败。");
        return Parse(output, id => { try { using var p = Process.GetProcessById(id); return p.ProcessName; } catch { return null; } }, entryPort);
    }
}
