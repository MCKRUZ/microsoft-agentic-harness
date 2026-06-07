using System.Net;

namespace Infrastructure.AI.Tests.Egress.Support;

/// <summary>
/// Terminal <see cref="HttpMessageHandler"/> for tests: returns the configured
/// response without performing any network I/O.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
    public int CallCount { get; private set; }
    public HttpRequestMessage? LastRequest { get; private set; }

    public StubHttpMessageHandler(HttpStatusCode status = HttpStatusCode.OK)
        : this(_ => new HttpResponseMessage(status))
    {
    }

    public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        CallCount++;
        LastRequest = request;
        return Task.FromResult(_responder(request));
    }
}
