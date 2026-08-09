using System.Net;
using System.Net.Sockets;
using System.Text;
using Azure;
using Azure.AI.Inference;
using Azure.AI.OpenAI;
using ChatRole = Microsoft.Extensions.AI.ChatRole;
using FluentAssertions;
using Infrastructure.AI.Helpers;
using Microsoft.Extensions.AI;
using Xunit;

namespace Infrastructure.AI.Tests.Resilience;

/// <summary>
/// Drives the real Azure OpenAI and Azure AI Inference clients against a stub HTTP server to
/// confirm that the retry-suppressing client options actually suppress retries.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately an integration-shaped test rather than an assertion that a property was
/// set. Both SDKs retry internally by default — measured at four requests for a single
/// rate-limited call — and that behaviour is reachable only by counting requests on the wire.
/// A test asserting <c>RetryPolicy is not null</c> would keep passing if a future SDK version
/// changed how the policy is honoured.
/// </para>
/// <para>
/// Each case is paired with the retrying default, so the assertion measures a difference rather
/// than a single number that could equally mean "the request never went out".
/// </para>
/// </remarks>
public sealed class ProviderSdkRetrySuppressionTests
{
    [Fact]
    public async Task AzureOpenAI_DefaultOptions_RetriesARateLimitInternally()
    {
        using var server = new StubHttpServer(HttpStatusCode.TooManyRequests);
        var chat = new AzureOpenAIClient(
                server.BaseAddress,
                new AzureKeyCredential("fake-key"),
                AgentFrameworkHelper.GetAzureOpenAIClientOptions())
            .GetChatClient("gpt-4o")
            .AsIChatClient();

        await Attempt(chat);

        server.RequestCount.Should().BeGreaterThan(
            1,
            "the bare client is the right place for SDK retry — nothing else wraps it");
    }

    [Fact]
    public async Task AzureOpenAI_RetrySuppressed_MakesExactlyOneRequest()
    {
        using var server = new StubHttpServer(HttpStatusCode.TooManyRequests);
        var chat = new AzureOpenAIClient(
                server.BaseAddress,
                new AzureKeyCredential("fake-key"),
                AgentFrameworkHelper.GetAzureOpenAIClientOptions(disableProviderRetry: true))
            .GetChatClient("gpt-4o")
            .AsIChatClient();

        await Attempt(chat);

        server.RequestCount.Should().Be(
            1,
            "inside the fallback chain the Polly pipeline is the only layer that may retry");
    }

    [Fact]
    public async Task AzureAIInference_DefaultOptions_RetriesARateLimitInternally()
    {
        using var server = new StubHttpServer(HttpStatusCode.TooManyRequests);
        var chat = new ChatCompletionsClient(server.BaseAddress, new AzureKeyCredential("fake-key"))
            .AsIChatClient("claude-sonnet");

        await Attempt(chat);

        server.RequestCount.Should().BeGreaterThan(1);
    }

    [Fact]
    public async Task AzureAIInference_RetrySuppressed_MakesExactlyOneRequest()
    {
        using var server = new StubHttpServer(HttpStatusCode.TooManyRequests);
        var options = new AzureAIInferenceClientOptions();
        options.Retry.MaxRetries = 0;
        var chat = new ChatCompletionsClient(server.BaseAddress, new AzureKeyCredential("fake-key"), options)
            .AsIChatClient("claude-sonnet");

        await Attempt(chat);

        server.RequestCount.Should().Be(1);
    }

    /// <summary>Issues one chat call and swallows the expected provider failure.</summary>
    private static async Task Attempt(IChatClient chat)
    {
        try
        {
            await chat.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);
        }
        catch
        {
            // The stub always fails; the measurement is the request count on the wire.
        }
    }

    /// <summary>
    /// A raw-TCP HTTP server answering every request with a fixed status, used instead of
    /// <c>HttpListener</c> so the test needs no URL-ACL registration on Windows.
    /// </summary>
    private sealed class StubHttpServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private int _requestCount;

        public StubHttpServer(HttpStatusCode status)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            BaseAddress = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");
            _ = Task.Run(() => AcceptLoopAsync(status, _cts.Token));
        }

        public Uri BaseAddress { get; }

        public int RequestCount => Volatile.Read(ref _requestCount);

        private async Task AcceptLoopAsync(HttpStatusCode status, CancellationToken ct)
        {
            const string body = """{"error":{"code":"stub","message":"stub failure"}}""";
            var response = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {(int)status} {status}\r\n"
                + "Content-Type: application/json\r\n"
                + $"Content-Length: {body.Length}\r\n"
                + "Connection: close\r\n\r\n"
                + body);

            while (!ct.IsCancellationRequested)
            {
                TcpClient tcp;
                try
                {
                    tcp = await _listener.AcceptTcpClientAsync(ct);
                }
                catch
                {
                    return;
                }

                _ = Task.Run(() => RespondAsync(tcp, response, ct), ct);
            }
        }

        private async Task RespondAsync(TcpClient tcp, byte[] response, CancellationToken ct)
        {
            using (tcp)
            {
                Interlocked.Increment(ref _requestCount);

                try
                {
                    var stream = tcp.GetStream();
                    using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    readCts.CancelAfter(TimeSpan.FromSeconds(2));
                    await stream.ReadAsync(new byte[8192], readCts.Token);
                    await stream.WriteAsync(response, ct);
                    await stream.FlushAsync(ct);
                }
                catch
                {
                    // A client that hangs up early still counted as a request, which is the
                    // only thing this server exists to measure.
                }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _cts.Dispose();
        }
    }
}
