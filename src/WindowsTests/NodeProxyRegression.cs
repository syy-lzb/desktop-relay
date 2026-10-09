using DesktopRelay.App;
using DesktopRelay.Core;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

static class NodeProxyRegression
{
    public static async Task RunAsync()
    {
        string node = Environment.GetEnvironmentVariable("RELAY_TEST_NODE") ?? throw new Exception("Set RELAY_TEST_NODE to a local Node 24.5+ executable for the regression test.");
        string script = Path.Combine(AppContext.BaseDirectory, "node-proxy-probe.cjs");
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=relay-probe.invalid",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1),DateTimeOffset.Now.AddDays(1));
        using var stop = new CancellationTokenSource();
        string fixture = Path.Combine(AppContext.BaseDirectory,"test-tls-fixture.json");
        File.WriteAllText(fixture,System.Text.Json.JsonSerializer.Serialize(new { cert=certificate.ExportCertificatePem(), key=rsa.ExportPkcs8PrivateKeyPem() }));
        var serverInfo=new ProcessStartInfo(node){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        serverInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory,"node-tls-origin.cjs"));serverInfo.ArgumentList.Add(fixture);
        using var originProcess=Process.Start(serverInfo)!;
        int originPort=int.Parse((await originProcess.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))!);
        var proxy = new TcpListener(IPAddress.Loopback,0); proxy.Start(); int proxyPort=((IPEndPoint)proxy.LocalEndpoint).Port;
        var clients = new System.Collections.Concurrent.ConcurrentBag<TcpClient>();
        var handlers = new System.Collections.Concurrent.ConcurrentBag<Task>(); int tunnels=0;
        async Task<string> Header(Stream s) { var text=new StringBuilder(); var one=new byte[1]; while(text.Length<16384&&!text.ToString().EndsWith("\r\n\r\n")){if(await s.ReadAsync(one,stop.Token)==0)break;text.Append((char)one[0]);}return text.ToString(); }
        async Task ServeProxy(TcpClient client)
        {
            using(client) using(var target=new TcpClient())
            {
                string header=await Header(client.GetStream());
                if(!header.StartsWith($"CONNECT relay-probe.invalid:{originPort} "))throw new Exception("Unexpected test CONNECT");
                Interlocked.Increment(ref tunnels); await target.ConnectAsync(IPAddress.Loopback,originPort,stop.Token);
                await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"),stop.Token);
                var up=client.GetStream().CopyToAsync(target.GetStream(),stop.Token); var down=target.GetStream().CopyToAsync(client.GetStream(),stop.Token);
                await Task.WhenAny(up,down); client.Dispose(); target.Dispose();
                try{await Task.WhenAll(up,down);}catch(IOException){}catch(ObjectDisposedException){}
            }
        }
        async Task Accept(TcpListener listener,Func<TcpClient,Task> serve)
        {
            try{while(!stop.IsCancellationRequested){var client=await listener.AcceptTcpClientAsync(stop.Token);clients.Add(client);handlers.Add(serve(client));}}catch(OperationCanceledException){}
        }
        var proxyLoop=Accept(proxy,ServeProxy);
        await using var relay=new RelayServer(); relay.Start(0); relay.SwitchRoute(new(RouteMode.HttpProxy,"127.0.0.1",proxyPort));
        async Task Run(bool useProxy)
        {
            var info=DesktopLauncher.BuildStartInfo(node,relay.Port); info.ArgumentList.Clear();
            info.ArgumentList.Add(script); info.ArgumentList.Add(originPort.ToString()); info.ArgumentList.Add(useProxy?"proxy":"control");
            info.Environment["NODE_USE_ENV_PROXY"]=useProxy?"1":"0"; info.CreateNoWindow=true; info.RedirectStandardOutput=true; info.RedirectStandardError=true;
            using var process=Process.Start(info)!;
            var output=process.StandardOutput.ReadToEndAsync(); var error=process.StandardError.ReadToEndAsync();
            try{await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));}catch{try{process.Kill();}catch{}throw;}
            string stdout=await output,stderr=await error;
            if(process.ExitCode!=0)throw new Exception($"Node probe failed {process.ExitCode}: {stderr}"); Console.Write(stdout);
        }
        try
        {
            await Run(false); if(tunnels!=0)throw new Exception("Control unexpectedly reached proxy");
            await Run(true); if(tunnels is < 1 or > 2)throw new Exception($"Expected one reused or two Relay CONNECTs, saw {tunnels}");
            Console.WriteLine($"PASS actual Relay CONNECT count={tunnels}; HTTPS and upgrade may reuse a pooled tunnel");
        }
        finally
        {
            stop.Cancel();proxy.Stop();foreach(var client in clients)client.Dispose();
            try { originProcess.Kill(); } catch(InvalidOperationException) {}
            await proxyLoop; try{await Task.WhenAll(handlers.ToArray());}catch(OperationCanceledException){}catch(IOException){}catch(ObjectDisposedException){}
        }
    }
}
