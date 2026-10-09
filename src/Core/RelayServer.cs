using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
namespace DesktopRelay.Core;
public sealed class RelayServer : IAsyncDisposable
{
    readonly Action<string>? log;
    readonly CancellationTokenSource stop = new();
    readonly ConcurrentDictionary<long, TcpClient> clients = new();
    readonly ConcurrentDictionary<long, Task> tasks = new();
    TcpListener? listener;
    Task? accept;
    long sequence;
    sealed record RouteState(Route Value, long Generation);
    readonly object routeGate = new();
    RouteState routeState = new(new(RouteMode.Disabled), 0);
    public RelayServer(Action<string>? log = null) => this.log = log;
    public int Port { get; private set; }
    public int ActiveConnections => clients.Count;
    public Route CurrentRoute => Volatile.Read(ref routeState).Value;
    public void Start(int port)
    {
        if (listener != null || stop.IsCancellationRequested) throw new InvalidOperationException("Relay cannot be started twice.");
        if (port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        var candidate = new TcpListener(IPAddress.Loopback, port);
        candidate.Start();
        Port = ((IPEndPoint)candidate.LocalEndpoint).Port;
        try { Validate(CurrentRoute); } catch { candidate.Stop(); Port = 0; throw; }
        listener = candidate; accept = AcceptAsync(); SafeLog($"Listener ready port={Port}");
    }
    void Validate(Route value)
    {
        if (value.Mode is RouteMode.Direct or RouteMode.Disabled) return;
        if (value.Mode != RouteMode.HttpProxy || string.IsNullOrWhiteSpace(value.Host) || value.Port is < 1 or > 65535 || value.Host.Any(c => char.IsWhiteSpace(c) || c is '/' or '\\' or '@' or '\r' or '\n')) throw new ArgumentException("Invalid HTTP proxy route.");
        if (Port != 0 && value.Port == Port && (value.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || (IPAddress.TryParse(value.Host, out var address) && IPAddress.IsLoopback(address)))) throw new ArgumentException("Proxy cannot point to this relay.");
    }
    public void SwitchRoute(Route value)
    {
        ArgumentNullException.ThrowIfNull(value); Validate(value);
        lock(routeGate) { var next = new RouteState(value, routeState.Generation + 1); Volatile.Write(ref routeState, next); SafeLog($"Route changed for new connections generation={next.Generation}"); }
    }
    void SafeLog(string message) { try { log?.Invoke(message); } catch { } }
    async Task AcceptAsync()
    {
        try {
            while (!stop.IsCancellationRequested) {
                var client = await listener!.AcceptTcpClientAsync(stop.Token);
                if (clients.Count >= 128) { client.Dispose(); continue; }
                var id = Interlocked.Increment(ref sequence); clients[id] = client;
                var snapshot = Volatile.Read(ref routeState);
                var task = HandleAsync(client, snapshot.Value, snapshot.Generation, id); tasks[id] = task;
                _ = task.ContinueWith(t => { tasks.TryRemove(id, out _); clients.TryRemove(id, out _); client.Dispose(); }, TaskScheduler.Default);
            }
        } catch (OperationCanceledException) {} catch (SocketException) when (stop.IsCancellationRequested) {}
    }
    static async Task<string> HeaderAsync(Stream stream, CancellationToken token)
    {
        var bytes = new List<byte>(); var one = new byte[1];
        while (bytes.Count < 16384) {
            if (await stream.ReadAsync(one, token) == 0) throw new IOException("Incomplete header");
            bytes.Add(one[0]); var n = bytes.Count;
            if (n >= 4 && bytes[n-4] == 13 && bytes[n-3] == 10 && bytes[n-2] == 13 && bytes[n-1] == 10) return Encoding.ASCII.GetString(bytes.ToArray());
        }
        throw new ArgumentException("Header too large");
    }
    static Task WriteAsync(Stream stream, string text, CancellationToken token) => stream.WriteAsync(Encoding.ASCII.GetBytes(text), token).AsTask();
    async Task HandleAsync(TcpClient client, Route snapshot, long generation, long id)
    {
        using var upstream = new TcpClient();
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); handshake.CancelAfter(TimeSpan.FromSeconds(10));
        var downstream = client.GetStream(); bool committed = false; bool parsed = false;
        string phase = "request-header";
        var clock = System.Diagnostics.Stopwatch.StartNew();
        int? upstreamStatus = null;
        try {
            var raw = await HeaderAsync(downstream, handshake.Token);
            if (snapshot.Mode == RouteMode.Disabled) { await WriteAsync(downstream,"HTTP/1.1 503 Service Unavailable\r\nConnection: close\r\nContent-Length: 0\r\n\r\n",handshake.Token); return; }
            var lines = raw.Split("\r\n"); var first = lines[0].Split(' ');
            if (first.Length != 3 || first[2] is not ("HTTP/1.1" or "HTTP/1.0") || !first[0].All(c => c is >= 'A' and <= 'Z')) throw new ArgumentException("Invalid request");
            var headers = new List<(string Name,string Value)>();
            foreach (var line in lines.Skip(1).Where(s => s.Length != 0)) {
                var colon = line.IndexOf(':'); if (colon <= 0 || line[0] is ' ' or '\t') throw new ArgumentException("Invalid header");
                var name = line[..colon]; if (!name.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) throw new ArgumentException("Invalid header name");
                var value = line[(colon+1)..].Trim(); if (value.Any(c => c < 32 && c != '\t' || c == 127)) throw new ArgumentException("Invalid header value");
                headers.Add((name,value));
            }
            bool connect = first[0] == "CONNECT";
            string host; int port; Uri? target = null;
            if (connect) {
                if (!Uri.TryCreate("http://" + first[1], UriKind.Absolute, out var authority) || authority.UserInfo.Length != 0 || authority.AbsolutePath != "/" || authority.Query.Length != 0 || authority.Fragment.Length != 0 || !first[1].Contains(':') || authority.Port is < 1 or > 65535) throw new ArgumentException("Invalid CONNECT authority");
                host = authority.Host; port = authority.Port;
            } else {
                if (!Uri.TryCreate(first[1], UriKind.Absolute, out target) || target.Scheme != "http" || target.UserInfo.Length != 0 || target.Fragment.Length != 0) throw new ArgumentException("Only absolute HTTP URLs supported");
                host = target.Host; port = target.Port;
            }
            if (port == Port && (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip))) throw new ArgumentException("Recursive target");
            var lengths = headers.Where(h => h.Name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)).ToArray();
            var transfers = headers.Where(h => h.Name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)).ToArray();
            long bodyLength = 0;
            if (lengths.Length > 1 || lengths.Length == 1 && (!lengths[0].Value.All(char.IsAsciiDigit) || !long.TryParse(lengths[0].Value, out bodyLength) || bodyLength < 0) || transfers.Length > 1 || transfers.Length == 1 && !transfers[0].Value.Equals("chunked", StringComparison.OrdinalIgnoreCase) || lengths.Length > 0 && transfers.Length > 0 || headers.Any(h => h.Name.Equals("Expect", StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Unsupported request framing");
            parsed = true;
            SafeLog($"Connection start id={id} generation={generation} mode={snapshot.Mode} method={first[0]} target={host}:{port}");
            phase = "upstream-tcp";
            await upstream.ConnectAsync(snapshot.Mode == RouteMode.HttpProxy ? snapshot.Host! : host, snapshot.Mode == RouteMode.HttpProxy ? snapshot.Port : port, handshake.Token);
            if (upstream.Client.RemoteEndPoint is IPEndPoint endpoint && endpoint.Port == Port && IPAddress.IsLoopback(endpoint.Address.IsIPv4MappedToIPv6 ? endpoint.Address.MapToIPv4() : endpoint.Address)) throw new IOException("Recursive resolved endpoint");
            var remote = upstream.GetStream();
            if (connect) {
                if (snapshot.Mode == RouteMode.HttpProxy) {
                    phase = "proxy-connect";
                    await WriteAsync(remote, $"CONNECT {first[1]} HTTP/1.1\r\nHost: {first[1]}\r\n\r\n", handshake.Token);
                    var response = await HeaderAsync(remote, handshake.Token); var status = response.Split("\r\n")[0].Split(' ');
                    if (status.Length < 2 || !int.TryParse(status[1], out int code)) throw new IOException("Invalid upstream CONNECT response");
                    upstreamStatus = code;
                    if (code < 200 || code > 299) throw new IOException("Upstream CONNECT rejected");
                }
                await WriteAsync(downstream, "HTTP/1.1 200 Connection Established\r\n\r\n", handshake.Token); committed = true;
                phase = "tunnel"; SafeLog($"Tunnel established id={id} generation={generation} elapsedMs={clock.ElapsedMilliseconds}");
                await TunnelAsync(client, upstream, stop.Token);
            } else {
                phase = "http-forward";
                var stripped = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Connection", "Proxy-Connection", "Proxy-Authorization", "Proxy-Authenticate", "Keep-Alive", "TE", "Trailer", "Upgrade", "Host", "Transfer-Encoding" };
                foreach (var h in headers.Where(h => h.Name.Equals("Connection", StringComparison.OrdinalIgnoreCase))) foreach (var name in h.Value.Split(',')) stripped.Add(name.Trim());
                if (stripped.Contains("Content-Length")) throw new ArgumentException("Connection header cannot nominate framing");
                var request = new StringBuilder($"{first[0]} {(snapshot.Mode == RouteMode.HttpProxy ? target!.AbsoluteUri : target!.PathAndQuery)} HTTP/1.1\r\nHost: {target.Authority}\r\nConnection: close\r\n");
                foreach (var h in headers.Where(h => !stripped.Contains(h.Name))) request.Append(h.Name).Append(": ").Append(h.Value).Append("\r\n");
                if (transfers.Length > 0) request.Append("Transfer-Encoding: chunked\r\n");
                request.Append("\r\n"); await WriteAsync(remote, request.ToString(), handshake.Token);
                // Body upload has no total timeout; stop cancellation still applies.
                var upload = UploadAsync(downstream, remote, bodyLength, transfers.Length > 0, stop.Token);
                var responseTask = ForwardResponseAsync(remote, downstream, handshake.Token, stop.Token, () => committed = true);
                var completed = await Task.WhenAny(upload, responseTask);
                await completed;
                if (completed == upload) await responseTask;
                else { client.Client.Shutdown(SocketShutdown.Receive); try { await upload; } catch (IOException) {} catch (SocketException) {} }
            }
        } catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ArgumentException or ObjectDisposedException) {
            string cause = ex is SocketException socket ? $"socket={socket.SocketErrorCode}" : ex is OperationCanceledException ? (stop.IsCancellationRequested ? "cancel=shutdown" : "cancel=handshake-deadline") : "";
            SafeLog($"Connection failed id={id} generation={generation} phase={phase} elapsedMs={clock.ElapsedMilliseconds} upstreamStatus={upstreamStatus?.ToString() ?? "none"} {cause}: {ex.GetType().Name}");
            if (!committed && !stop.IsCancellationRequested) try { using var error = new CancellationTokenSource(TimeSpan.FromSeconds(1)); await WriteAsync(downstream, $"HTTP/1.1 {(parsed ? "502 Bad Gateway" : "400 Bad Request")}\r\nConnection: close\r\nContent-Length: 0\r\n\r\n", error.Token); } catch { }
        }
        finally { SafeLog($"Connection end id={id} generation={generation} phase={phase} elapsedMs={clock.ElapsedMilliseconds}"); }
    }
    static async Task ForwardResponseAsync(Stream source, Stream destination, CancellationToken handshake, CancellationToken lifetime, Action commit)
    {
        // A first response must arrive within the handshake deadline. Subsequent body streams are unbounded.
        for (int informational = 0; ; informational++) {
        if (informational > 8) throw new IOException("Too many informational responses");
        var header = await HeaderAsync(source, handshake);
        var lines = header.Split("\r\n");
        if (!lines[0].StartsWith("HTTP/1.") || lines[0].Split(' ').Length < 2 || !int.TryParse(lines[0].Split(' ')[1], out var statusCode) || statusCode < 100 || statusCode > 599 || statusCode == 101) throw new IOException("Invalid upstream HTTP response");
        if(statusCode < 200 && informational >= 8) throw new IOException("Too many informational responses");
        var fields = new List<(string Name, string Value)>();
        foreach (var line in lines.Skip(1).Where(x => x.Length > 0)) { var colon=line.IndexOf(':'); if(colon<=0) throw new IOException("Invalid upstream header"); fields.Add((line[..colon], line[(colon+1)..].Trim())); }
        var strip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Connection", "Proxy-Connection", "Proxy-Authenticate", "Proxy-Authorization", "Keep-Alive", "TE", "Upgrade" };
        foreach(var h in fields.Where(x=>x.Name.Equals("Connection",StringComparison.OrdinalIgnoreCase))) foreach(var name in h.Value.Split(',')) strip.Add(name.Trim());
        // Framing remains intact even if a broken server nominates it as a connection option.
        strip.Remove("Content-Length"); strip.Remove("Transfer-Encoding"); strip.Remove("Trailer");
        var clean = new StringBuilder(lines[0]+"\r\nConnection: close\r\n");
        foreach(var h in fields.Where(x=>!strip.Contains(x.Name))) clean.Append(h.Name).Append(": ").Append(h.Value).Append("\r\n");
        clean.Append("\r\n"); await WriteAsync(destination,clean.ToString(),handshake);
        if(statusCode < 200) continue;
        commit();
        await source.CopyToAsync(destination,lifetime);
        return;
        }
    }
    static async Task UploadAsync(Stream input, Stream output, long length, bool chunked, CancellationToken token)
    {
        var buffer = new byte[32768];
        async Task Copy(long count) { while (count > 0) { var n = await input.ReadAsync(buffer.AsMemory(0,(int)Math.Min(buffer.Length,count)), token); if (n == 0) throw new IOException("Truncated body"); await output.WriteAsync(buffer.AsMemory(0,n),token); count -= n; } }
        if (!chunked) { await Copy(length); return; }
        async Task<string> Line() { var list = new List<byte>(); var b = new byte[1]; while (list.Count < 8192) { if (await input.ReadAsync(b,token) == 0) throw new IOException("Truncated chunk"); list.Add(b[0]); if (list.Count >= 2 && list[^2] == 13 && list[^1] == 10) return Encoding.ASCII.GetString(list.ToArray()); } throw new IOException("Chunk line too long"); }
        while (true) { var line = await Line(); if (!long.TryParse(line.Trim().Split(';')[0], System.Globalization.NumberStyles.HexNumber, null, out var size) || size < 0) throw new IOException("Invalid chunk"); await WriteAsync(output,line,token); if (size == 0) { var trailer = await Line(); if (trailer != "\r\n") throw new IOException("Chunk trailers unsupported"); await WriteAsync(output,trailer,token); return; } await Copy(size); var end = await Line(); if (end != "\r\n") throw new IOException("Invalid chunk ending"); await WriteAsync(output,end,token); }
    }
    static async Task TunnelAsync(TcpClient left, TcpClient right, CancellationToken token)
    {
        var leftStream=left.GetStream(); var rightStream=right.GetStream();
        async Task Pump(Stream source, Stream destinationStream, TcpClient destination) { try { await source.CopyToAsync(destinationStream,token); try { destination.Client.Shutdown(SocketShutdown.Send); } catch (SocketException) {} } catch { left.Dispose(); right.Dispose(); throw; } }
        await Task.WhenAll(Pump(leftStream,rightStream,right),Pump(rightStream,leftStream,left));
    }
    public async Task StopAsync() { stop.Cancel(); listener?.Stop(); foreach (var client in clients.Values) client.Dispose(); if (accept != null) await accept; try { await Task.WhenAll(tasks.Values); } catch (Exception ex) when (stop.IsCancellationRequested && ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { } }
    public async ValueTask DisposeAsync() { await StopAsync(); stop.Dispose(); }
}
