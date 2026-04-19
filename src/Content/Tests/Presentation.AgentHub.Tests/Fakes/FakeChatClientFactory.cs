using Application.AI.Common.Interfaces;
using Domain.Common.Config.AI;

namespace Presentation.AgentHub.Tests.Fakes;

/// <summary>
/// In-memory <see cref="IChatClientFactory"/> that returns <see cref="FakeChatClient"/> instances.
/// Configurable per-deployment or with a shared default client.
/// </summary>
public sealed class FakeChatClientFactory : IChatClientFactory
{
    private readonly Dictionary<string, FakeChatClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly FakeChatClient _defaultClient = new();

    /// <summary>The default client returned when no deployment-specific client is registered.</summary>
    public FakeChatClient DefaultClient => _defaultClient;

    /// <summary>Registers a fake client for a specific deployment/agent ID.</summary>
    public FakeChatClientFactory WithClient(string deploymentOrAgentId, FakeChatClient client)
    {
        _clients[deploymentOrAgentId] = client;
        return this;
    }

    /// <inheritdoc />
    public bool IsAvailable(AIAgentFrameworkClientType clientType) => true;

    /// <inheritdoc />
    public Task<Microsoft.Extensions.AI.IChatClient> GetChatClientAsync(
        AIAgentFrameworkClientType clientType,
        string deploymentOrAgentId,
        CancellationToken cancellationToken = default)
    {
        var client = _clients.TryGetValue(deploymentOrAgentId, out var specific)
            ? specific
            : _defaultClient;
        return Task.FromResult<Microsoft.Extensions.AI.IChatClient>(client);
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<AIAgentFrameworkClientType, bool> GetAvailableProviders() =>
        new Dictionary<AIAgentFrameworkClientType, bool>
        {
            [AIAgentFrameworkClientType.AzureOpenAI] = true,
            [AIAgentFrameworkClientType.OpenAI] = true,
        };

    /// <inheritdoc />
    public Task<string> CreatePersistentAgentAsync(
        string model, string name, string? instructions = null,
        string? description = null, CancellationToken cancellationToken = default) =>
        Task.FromResult($"fake-agent-{Guid.NewGuid():N}");
}
