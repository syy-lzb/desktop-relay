namespace DesktopRelay.Core;
public enum RouteMode { HttpProxy, Direct, Disabled }
public sealed record Route(RouteMode Mode, string? Host = null, int Port = 0)
{
    public static Route ParseProxy(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("HTTP proxy address is required.");
        if (!text.Contains("://")) text = "http://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != "http" || string.IsNullOrEmpty(uri.Host) || uri.Port is < 1 or > 65535 || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Use an HTTP proxy host:port without credentials, path or query.");
        return new(RouteMode.HttpProxy, uri.Host, uri.Port);
    }
}
