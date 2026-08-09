using Application.AI.Common.Interfaces;
using Application.AI.Common.Models;
using Domain.Common.Config.AI;
using Microsoft.Extensions.AI;

namespace Application.AI.Common.Tests.Fakes;

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
    public Task<IChatClient> GetChatClientAsync(
        AIAgentFrameworkClientType clientType,
        string deploymentOrAgentId,
        CancellationToken cancellationToken = default)
    {
        var client = _clients.TryGetValue(deploymentOrAgentId, out var specific)
            ? specific
            : _defaultClient;
        return Task.FromResult<IChatClient>(client);
    }

    /// <inheritdoc />
    /// <remarks>The fakes have no SDK retry to disable, so this yields the same client.</remarks>
    public Task<IChatClient> GetChatClientWithoutProviderRetryAsync(
        AIAgentFrameworkClientType clientType,
        string deploymentOrAgentId,
        CancellationToken cancellationToken = default)
        => GetChatClientAsync(clientType, deploymentOrAgentId, cancellationToken);

    /// <inheritdoc />
    public IReadOnlyDictionary<AIAgentFrameworkClientType, bool> GetAvailableProviders() =>
        new Dictionary<AIAgentFrameworkClientType, bool>
        {
            [AIAgentFrameworkClientType.AzureOpenAI] = true,
            [AIAgentFrameworkClientType.OpenAI] = true,
        };

    /// <inheritdoc />
    public AiProviderStatus GetProviderStatus() =>
        new(AIAgentFrameworkClientType.AzureOpenAI, "fake-deployment", IsConfigured: true, MissingSettings: []);

    /// <inheritdoc />
    public Task<string> CreatePersistentAgentAsync(
        string model, string name, string? instructions = null,
        string? description = null, CancellationToken cancellationToken = default) =>
        Task.FromResult($"fake-agent-{Guid.NewGuid():N}");
}
