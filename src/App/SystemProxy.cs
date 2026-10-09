using System;
using System.Collections.Generic;
using Microsoft.Win32;
using DesktopRelay.Core;

namespace DesktopRelay.App;
public static class SystemProxy
{
    public static string Fingerprint()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        return $"{key?.GetValue("ProxyEnable")}|{key?.GetValue("ProxyServer")}|{key?.GetValue("AutoConfigURL")}";
    }
    public static Route Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        return Parse(Convert.ToInt32(key?.GetValue("ProxyEnable") ?? 0) == 1, key?.GetValue("ProxyServer") as string ?? "");
    }
    public static Route Parse(bool enabled, string proxyServer)
    {
        if (!enabled)
            throw new InvalidOperationException("系统代理未开启。可手动选择直连 / TUN 或填写自定义代理。");
        string value = proxyServer.Trim();
        if (string.IsNullOrEmpty(value)) throw new InvalidOperationException("没有静态代理地址。首版不支持 PAC 自动配置。");
        if (value.Contains('='))
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in value.Split(';'))
            {
                var pair = item.Split('=', 2);
                if (pair.Length == 2) map[pair[0].Trim()] = pair[1].Trim();
            }
            map.TryGetValue("http", out string? http); map.TryGetValue("https", out string? https);
            if (http != null && https != null && !http.Equals(https, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("HTTP 与 HTTPS 上游不同，首版只支持共用一个 HTTP 入口。");
            value = http ?? https ?? throw new InvalidOperationException("仅 SOCKS 的配置不受支持，请使用 HTTP / 混合入口。");
        }
        return Route.ParseProxy(value);
    }
}
