using Application.AI.Common.Interfaces;
using Application.AI.Common.Models;
using Domain.Common.Config.AI;
using Microsoft.Extensions.AI;

namespace Tests.AI.Fakes;

/// <summary>
/// An <see cref="IChatClientFactory"/> whose every client is a <see cref="SimulatedModelChatClient"/>
/// imitating one <see cref="SimulatedModelProfile"/>, for any provider, deployment or agent.
/// </summary>
/// <remarks>
/// <para>
/// Replaces the real factory in a load-test host so every agent turn, from a bundle run or a
/// workflow LLM step, costs the time a real model call would without contacting one. Unlike
/// <see cref="ScriptedChatClientFactory"/> it needs no ambient request scope and no per-role
/// script, because a load test cares how long the model takes, not what it says.
/// </para>
/// <para>
/// Register it as a singleton. It holds only the immutable profile and shared clock, and each
/// call gets its own client, so concurrent agent turns share nothing mutable.
/// </para>
/// </remarks>
public sealed class SimulatedModelChatClientFactory : IChatClientFactory
{
    private readonly SimulatedModelProfile _profile;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a factory whose clients imitate <paramref name="profile"/>.</summary>
    /// <param name="profile">The timing and failure behaviour every client imitates.</param>
    /// <param name="timeProvider">Clock and delays; <see cref="TimeProvider.System"/> outside tests.</param>
    public SimulatedModelChatClientFactory(SimulatedModelProfile profile, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The profile every client this factory builds imitates.</summary>
    public SimulatedModelProfile Profile => _profile;

    /// <inheritdoc />
    public bool IsAvailable(AIAgentFrameworkClientType clientType) => true;

    /// <inheritdoc />
    public Task<IChatClient> GetChatClientAsync(
        AIAgentFrameworkClientType clientType,
        string deploymentOrAgentId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IChatClient>(new SimulatedModelChatClient(_profile, _timeProvider));

    /// <inheritdoc />
    /// <remarks>The simulation has no SDK retry to disable, so this yields the same kind of client.</remarks>
    public Task<IChatClient> GetChatClientWithoutProviderRetryAsync(
        AIAgentFrameworkClientType clientType,
        string deploymentOrAgentId,
        CancellationToken cancellationToken = default) =>
        GetChatClientAsync(clientType, deploymentOrAgentId, cancellationToken);

    /// <inheritdoc />
    public IReadOnlyDictionary<AIAgentFrameworkClientType, bool> GetAvailableProviders() =>
        new Dictionary<AIAgentFrameworkClientType, bool> { [AIAgentFrameworkClientType.AzureOpenAI] = true };

    /// <inheritdoc />
    public AiProviderStatus GetProviderStatus() =>
        new(AIAgentFrameworkClientType.AzureOpenAI, $"simulated-{_profile.Name}", IsConfigured: true, MissingSettings: []);

    /// <inheritdoc />
    public Task<string> CreatePersistentAgentAsync(
        string model,
        string name,
        string? instructions = null,
        string? description = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult($"simulated-agent-{Guid.NewGuid():N}");
}
