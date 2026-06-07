using System.Net;
using System.Text;

namespace Infrastructure.AI.Tests.Egress.Support;

/// <summary>
/// Tiny <see cref="HttpListener"/>-backed server bound to a random loopback
/// port. Used by acceptance tests as a controllable upstream — both the
/// outer-policy-only path (allow-flow) and the redirect-to-private-IP test
/// drive requests through it.
/// </summary>
internal sealed class LocalHttpListener : IDisposable
{
    private readonly HttpListener _listener;
    private readonly Task _acceptLoop;
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<HttpListenerContext, Task> _handler;

    public string BaseUrl { get; }

    public LocalHttpListener(Func<HttpListenerContext, Task> handler)
    {
        _handler = handler;
        var port = GetFreePort();
        BaseUrl = $"http://127.0.0.1:{port}/";
        _listener = new HttpListener();
        _listener.Prefixes.Add(BaseUrl);
        _listener.Start();
        _acceptLoop = AcceptLoop(_cts.Token);
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext? ctx = null;
            try
            {
                ctx = await _listener.GetContextAsync().WaitAsync(ct);
            }
            catch (OperationCanceledException) { return; }
            catch (HttpListenerException) { return; }
            catch (ObjectDisposedException) { return; }

            if (ctx is null) continue;

            _ = Task.Run(async () =>
            {
                try { await _handler(ctx); }
                catch { try { ctx.Response.StatusCode = 500; } catch { } }
                finally { try { ctx.Response.OutputStream.Close(); } catch { } }
            }, ct);
        }
    }

    public static Task RespondOk(HttpListenerContext ctx, string body = "ok")
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/plain";
        ctx.Response.ContentLength64 = bytes.Length;
        return ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
    }

    public static Task RespondRedirect(HttpListenerContext ctx, string location)
    {
        ctx.Response.StatusCode = 302;
        ctx.Response.RedirectLocation = location;
        return Task.CompletedTask;
    }

    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
        try { _acceptLoop.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
    }
}
