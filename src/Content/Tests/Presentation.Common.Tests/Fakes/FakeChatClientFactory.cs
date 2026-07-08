using Application.AI.Common.Interfaces;
using Application.AI.Common.Models;
using Domain.Common.Config.AI;
using Microsoft.Extensions.AI;

namespace Presentation.Common.Tests.Fakes;

/// <summary>
/// In-memory <see cref="IChatClientFactory"/> for composition-root tests. Replaces ONLY the
/// external LLM boundary (the production <c>ChatClientFactory</c> would build a real Azure
/// OpenAI client); everything else in the graph stays production wiring.
/// </summary>
public sealed class FakeChatClientFactory : IChatClientFactory
{
    private readonly FakeChatClient _client = new();

    /// <inheritdoc />
    public bool IsAvailable(AIAgentFrameworkClientType clientType) => true;

    /// <inheritdoc />
    public Task<IChatClient> GetChatClientAsync(
        AIAgentFrameworkClientType clientType,
        string deploymentOrAgentId,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IChatClient>(_client);

    /// <inheritdoc />
    public IReadOnlyDictionary<AIAgentFrameworkClientType, bool> GetAvailableProviders() =>
        new Dictionary<AIAgentFrameworkClientType, bool>
        {
            [AIAgentFrameworkClientType.AzureOpenAI] = true,
        };

    /// <inheritdoc />
    public AiProviderStatus GetProviderStatus() =>
        new(AIAgentFrameworkClientType.AzureOpenAI, "fake-deployment", IsConfigured: true, MissingSettings: []);

    /// <inheritdoc />
    public Task<string> CreatePersistentAgentAsync(
        string model, string name, string? instructions = null,
        string? description = null, CancellationToken cancellationToken = default)
        => Task.FromResult($"fake-agent-{Guid.NewGuid():N}");

    /// <summary>Minimal <see cref="IChatClient"/> that returns a canned assistant response.</summary>
    private sealed class FakeChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "fake response")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var message in response.Messages)
                yield return new ChatResponseUpdate(message.Role, message.Contents);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
            // Nothing to release — the fake holds no unmanaged resources.
        }
    }
}
