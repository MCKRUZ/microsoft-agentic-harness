using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using Azure;
using Azure.AI.OpenAI;
using Azure.AI.Projects;
using FluentAssertions;
using Infrastructure.AI.Factories;
using Infrastructure.AI.Helpers;
using Infrastructure.AI.Tests.Helpers;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.Factories;

/// <summary>
/// Constructs each Azure SDK surface the harness still uses and sends one stubbed request through it.
/// Azure.AI.OpenAI's newest published version (2.9.0-beta.1) is compiled against an older OpenAI
/// library, and Azure.AI.Projects moved a major version with the Agent Framework upgrade, so any
/// surface can throw <see cref="MissingMethodException"/> at runtime once a dependency moves on — a
/// failure no compiler check and no DI container validation can see (the Responses surface did
/// exactly that; see the Foundry direct-Responses client). Sending a request is the only check that
/// exercises the constructors.
/// </summary>
public sealed class AzureOpenAiSurfaceCompatibilityTests
{
    private static readonly Uri Endpoint = new("https://myresource.openai.azure.com");

    [Fact]
    public async Task ChatClient_ConstructsAndCompletesARequest()
    {
        var handler = new RoutingHandler();
        var chat = Client(handler).GetChatClient("my-deployment").AsIChatClient();

        var response = await chat.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        response.Text.Should().Be("hello back");
        handler.Paths.Should().ContainSingle()
            .Which.Should().Contain("/openai/deployments/my-deployment/chat/completions");
    }

    [Fact]
    public async Task ChatClient_Streaming_ConstructsAndCompletesARequest()
    {
        // The agent loop streams; the streaming path uses different SDK members than a one-shot call.
        var chat = Client(new RoutingHandler()).GetChatClient("my-deployment").AsIChatClient();

        var text = new StringBuilder();
        await foreach (var update in chat.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
            text.Append(update.Text);

        text.ToString().Should().Be("hello back");
    }

    [Fact]
    public async Task EmbeddingClient_ConstructsAndCompletesARequest()
    {
        var handler = new RoutingHandler();
        var embeddings = Client(handler).GetEmbeddingClient("my-embedding").AsIEmbeddingGenerator();

        var result = await embeddings.GenerateAsync(["hello"]);

        result.Should().ContainSingle().Which.Vector.Length.Should().Be(2);
        handler.Paths.Should().ContainSingle()
            .Which.Should().Contain("/openai/deployments/my-embedding/embeddings");
    }

    [Fact]
    public async Task FoundryProjectAgent_ConstructsAndCompletesARequest()
    {
        // The Project-scoped FoundryResponses path: AIProjectClient -> AsAIAgent -> a Responses turn.
        var handler = new RoutingHandler();
        var options = new AIProjectClientOptions
        {
            Transport = new HttpClientPipelineTransport(new HttpClient(handler))
        };
        var project = new AIProjectClient(
            new Uri("https://myresource.services.ai.azure.com/api/projects/my-project"),
            new StaticTokenCredential("fake-token"),
            options);
        var provider = new FoundryAgentProvider(
            project, NullLoggerFactory.Instance, new ServiceCollection().BuildServiceProvider());

        var agent = await provider.CreateAgentAsync(
            "my-deployment", new ChatClientAgentOptions { Name = "probe" }, client => client);
        var response = await agent.RunAsync("hi");

        response.Text.Should().Be("hello back");
        handler.Paths.Should().Contain(p => p.Contains("responses", StringComparison.OrdinalIgnoreCase));
    }

    private static AzureOpenAIClient Client(HttpMessageHandler handler)
    {
        var options = AgentFrameworkHelper.GetAzureOpenAIClientOptions(disableProviderRetry: true);
        options.Transport = new HttpClientPipelineTransport(new HttpClient(handler));
        return new AzureOpenAIClient(Endpoint, new AzureKeyCredential("fake-key"), options);
    }

    /// <summary>
    /// Answers chat-completions (one-shot or streamed), embeddings and Responses requests with a
    /// minimal valid body, by path, and records every path that reached the wire.
    /// </summary>
    private sealed class RoutingHandler : HttpMessageHandler
    {
        private const string ChatBody = """
            {"id":"c1","object":"chat.completion","created":1700000000,"model":"my-deployment",
             "choices":[{"index":0,"message":{"role":"assistant","content":"hello back"},"finish_reason":"stop"}],
             "usage":{"prompt_tokens":1,"completion_tokens":2,"total_tokens":3}}
            """;

        private const string ChatStream =
            """data: {"id":"c1","object":"chat.completion.chunk","created":1700000000,"model":"my-deployment","choices":[{"index":0,"delta":{"role":"assistant","content":"hello back"},"finish_reason":null}]}"""
            + "\n\n"
            + """data: {"id":"c1","object":"chat.completion.chunk","created":1700000000,"model":"my-deployment","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}"""
            + "\n\ndata: [DONE]\n\n";

        private const string EmbeddingBody = """
            {"object":"list","model":"my-embedding",
             "data":[{"object":"embedding","index":0,"embedding":[0.1,0.2]}],
             "usage":{"prompt_tokens":1,"total_tokens":1}}
            """;

        private const string ResponsesBody = """
            {"id":"resp_1","object":"response","created_at":1700000000,"status":"completed","model":"my-deployment",
             "output":[{"type":"message","id":"msg_1","status":"completed","role":"assistant",
                        "content":[{"type":"output_text","text":"hello back","annotations":[]}]}],
             "usage":{"input_tokens":1,"output_tokens":2,"total_tokens":3}}
            """;

        private readonly List<string> _paths = [];

        public IReadOnlyList<string> Paths
        {
            get { lock (_paths) return [.. _paths]; }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            lock (_paths) _paths.Add(path);

            var requestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            if (path.Contains("embeddings", StringComparison.OrdinalIgnoreCase))
                return Json(EmbeddingBody);

            if (path.Contains("responses", StringComparison.OrdinalIgnoreCase))
                return Json(ResponsesBody);

            return requestBody.Contains("\"stream\":true", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(ChatStream, Encoding.UTF8, "text/event-stream")
                }
                : Json(ChatBody);
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
