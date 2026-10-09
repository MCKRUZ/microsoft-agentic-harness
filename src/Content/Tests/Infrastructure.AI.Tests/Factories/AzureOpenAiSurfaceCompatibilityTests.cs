using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using Azure;
using Azure.AI.OpenAI;
using FluentAssertions;
using Infrastructure.AI.Helpers;
using Microsoft.Extensions.AI;
using Xunit;

namespace Infrastructure.AI.Tests.Factories;

/// <summary>
/// Constructs each remaining <see cref="AzureOpenAIClient"/> surface the harness uses and sends one
/// stubbed request through it. Azure.AI.OpenAI's newest published version (2.9.0-beta.1) is compiled
/// against an older OpenAI library, so any surface can throw <see cref="MissingMethodException"/>
/// at runtime once the OpenAI library moves on — a failure no compiler check and no DI container
/// validation can see (the Responses surface did exactly that; see the Foundry direct-Responses
/// client). Sending a request is the only check that exercises the constructors.
/// </summary>
public sealed class AzureOpenAiSurfaceCompatibilityTests
{
    private static readonly Uri Endpoint = new("https://myresource.openai.azure.com");

    [Fact]
    public async Task ChatClient_ConstructsAndCompletesARequest()
    {
        var chat = Client(new RoutingHandler()).GetChatClient("my-deployment").AsIChatClient();

        var response = await chat.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        response.Text.Should().Be("hello back");
    }

    [Fact]
    public async Task EmbeddingClient_ConstructsAndCompletesARequest()
    {
        var embeddings = Client(new RoutingHandler()).GetEmbeddingClient("my-embedding").AsIEmbeddingGenerator();

        var result = await embeddings.GenerateAsync(["hello"]);

        result.Should().ContainSingle()
            .Which.Vector.Length.Should().Be(2);
    }

    private static AzureOpenAIClient Client(HttpMessageHandler handler)
    {
        var options = AgentFrameworkHelper.GetAzureOpenAIClientOptions(disableProviderRetry: true);
        options.Transport = new HttpClientPipelineTransport(new HttpClient(handler));
        return new AzureOpenAIClient(Endpoint, new AzureKeyCredential("fake-key"), options);
    }

    /// <summary>Answers a chat-completions or an embeddings request with a minimal valid body, by path.</summary>
    private sealed class RoutingHandler : HttpMessageHandler
    {
        private const string ChatBody = """
            {"id":"c1","object":"chat.completion","created":1700000000,"model":"my-deployment",
             "choices":[{"index":0,"message":{"role":"assistant","content":"hello back"},"finish_reason":"stop"}],
             "usage":{"prompt_tokens":1,"completion_tokens":2,"total_tokens":3}}
            """;

        private const string EmbeddingBody = """
            {"object":"list","model":"my-embedding",
             "data":[{"object":"embedding","index":0,"embedding":[0.1,0.2]}],
             "usage":{"prompt_tokens":1,"total_tokens":1}}
            """;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.RequestUri!.AbsolutePath.Contains("embeddings", StringComparison.OrdinalIgnoreCase)
                ? EmbeddingBody
                : ChatBody;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
