using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using FluentAssertions;
using Infrastructure.AI.Helpers;
using Infrastructure.AI.Tests.Helpers;
using Microsoft.Extensions.AI;
using Xunit;

namespace Infrastructure.AI.Tests.Factories;

/// <summary>
/// Drives the real OpenAI SDK <c>ResponsesClient</c> the FoundryDirectResponses client type is
/// built on, against a captured transport, to pin what reaches the wire: the <c>/openai/v1/</c>
/// route, the Entra bearer token and scope, the deployment as the model, and retry behaviour.
/// </summary>
/// <remarks>
/// The previous implementation (<c>AzureOpenAIClient.GetResponsesClient()</c>) built fine and then
/// threw <see cref="MissingMethodException"/> at runtime once the OpenAI library moved on, which no
/// compile-time check can see. Constructing the client and sending one request is the only check
/// that would have caught it, so that is what these tests do.
/// </remarks>
public sealed class FoundryDirectResponsesClientTests
{
    private static readonly Uri Resource = new("https://myresource.services.ai.azure.com");

    [Fact]
    public async Task Request_GoesToTheV1ResponsesRoute_WithTheBearerTokenAndDeploymentAsModel()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, SuccessBody);
        var credential = new StaticTokenCredential("entra-token");
        var chat = Build(handler, credential);

        var response = await chat.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        handler.Requests.Should().ContainSingle();
        var request = handler.Requests[0];
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri.Should().Be("https://myresource.services.ai.azure.com/openai/v1/responses");
        request.Authorization.Should().Be("Bearer entra-token");
        request.Body.Should().Contain("\"model\":\"my-deployment\"");
        credential.RequestedScopes.Should().ContainSingle()
            .Which.Should().Be(
                "https://cognitiveservices.azure.com/.default",
                "the audience AzureOpenAIClient requested must not change when the client does");
        response.Text.Should().Be("hello back");
    }

    [Fact]
    public async Task RateLimit_DefaultClient_RetriesInternally()
    {
        var handler = new CapturingHandler(HttpStatusCode.TooManyRequests, ErrorBody);

        await Attempt(Build(handler, new StaticTokenCredential("t")));

        handler.Requests.Count.Should().BeGreaterThan(
            1, "the bare client is the right place for SDK retry — nothing else wraps it");
    }

    [Fact]
    public async Task RateLimit_RetrySuppressedClient_MakesExactlyOneRequest()
    {
        var handler = new CapturingHandler(HttpStatusCode.TooManyRequests, ErrorBody);

        await Attempt(Build(handler, new StaticTokenCredential("t"), disableProviderRetry: true));

        handler.Requests.Count.Should().Be(
            1, "inside the fallback chain the Polly pipeline is the only layer that may retry");
    }

    [Fact]
    public async Task EndpointWithATrailingSlash_StillResolvesToTheV1Route()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, SuccessBody);
        var chat = Build(handler, new StaticTokenCredential("t"), endpoint: new Uri(Resource, "/"));

        await chat.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        handler.Requests.Should().ContainSingle()
            .Which.Uri.Should().Be("https://myresource.services.ai.azure.com/openai/v1/responses");
    }

    private static IChatClient Build(
        CapturingHandler handler,
        StaticTokenCredential credential,
        bool disableProviderRetry = false,
        Uri? endpoint = null) =>
        AgentFrameworkHelper.CreateFoundryDirectResponsesClient(
                endpoint ?? Resource,
                credential,
                disableProviderRetry,
                new HttpClientPipelineTransport(new HttpClient(handler)))
            .AsIChatClient("my-deployment");

    /// <summary>Issues one call and swallows the expected provider failure; the measurement is the request count.</summary>
    private static async Task Attempt(IChatClient chat)
    {
        try
        {
            await chat.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);
        }
        catch
        {
            // The stub always fails.
        }
    }

    private const string ErrorBody = """{"error":{"code":"stub","message":"stub failure"}}""";

    private const string SuccessBody = """
        {"id":"resp_1","object":"response","created_at":1700000000,"status":"completed","model":"my-deployment",
         "output":[{"type":"message","id":"msg_1","status":"completed","role":"assistant",
                    "content":[{"type":"output_text","text":"hello back","annotations":[]}]}],
         "usage":{"input_tokens":1,"output_tokens":2,"total_tokens":3}}
        """;

    private sealed record CapturedRequest(HttpMethod Method, string Uri, string? Authorization, string Body);

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        private readonly List<CapturedRequest> _requests = [];

        public CapturingHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        public IReadOnlyList<CapturedRequest> Requests
        {
            get { lock (_requests) return [.. _requests]; }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            lock (_requests)
                _requests.Add(new CapturedRequest(
                    request.Method, request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), body));

            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            };
        }
    }
}
