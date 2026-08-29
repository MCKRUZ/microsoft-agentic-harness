using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Models;
using Domain.Common.Config.AI;
using Microsoft.Extensions.AI;

namespace Tests.AI.Fakes;

/// <summary>
/// Role-aware <see cref="IChatClientFactory"/> fake. Selects which <see cref="RoleScript"/> a call
/// gets by reading <see cref="IAgentExecutionContext.AgentId"/> — the ambient identity
/// <c>AgentContextPropagationBehavior</c> stamps before a handler runs, and which is already in
/// scope by the time <c>AgentFactory</c> calls <c>GetChatClientAsync</c> — rather than by sniffing
/// prompt text.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Lifetime.</strong> Must be registered <c>Scoped</c>, not the production
/// <c>Singleton</c> lifetime <c>IChatClientFactory</c> normally has — it depends on the scoped
/// <see cref="IAgentExecutionContext"/>. A singleton registration is only guaranteed to throw when
/// the container has scope validation enabled (<c>ServiceProviderOptions.ValidateScopes = true</c>,
/// on by default for an ASP.NET Core host but <em>not</em> for a bare
/// <c>new ServiceCollection().BuildServiceProvider()</c>). Build any hand-rolled test container for
/// this factory with <c>ValidateScopes = true</c> — otherwise a lifetime mistake here doesn't throw,
/// it silently freezes every scope onto whichever <see cref="IAgentExecutionContext"/> was active on
/// first resolution, which reads as role-bleed between tests rather than a lifetime bug. Register
/// the <see cref="ChatInvocationLog"/> singleton separately so it survives across scopes and can
/// still answer "what was the full call sequence for this test."
/// </para>
/// <para>
/// <strong>Fails loudly on an unscripted role.</strong> A call whose resolved <c>AgentId</c> has no
/// registered <see cref="RoleScript"/> and no <see cref="ForAnyUnscriptedRole"/> fallback throws,
/// naming the unmatched id and every registered role — the same "must fail loudly, not return empty
/// success" requirement structured-output and pipeline scenarios are built to prove.
/// </para>
/// </remarks>
public sealed class ScriptedChatClientFactory(IAgentExecutionContext executionContext, ChatInvocationLog log) : IChatClientFactory
{
    private readonly Dictionary<string, RoleScript> _roles = new(StringComparer.Ordinal);
    private RoleScript? _anyRoleFallback;

    /// <summary>Registers (or returns the existing) script for the given agent id.</summary>
    public RoleScript ForRole(string agentId)
    {
        if (_roles.TryGetValue(agentId, out var existing)) return existing;
        var script = new RoleScript();
        _roles[agentId] = script;
        return script;
    }

    /// <summary>
    /// Registers a fallback script used for any call whose agent id has no dedicated
    /// <see cref="ForRole"/> script. An explicit opt-in, not a silent default — a test that wants
    /// role separation enforced simply never calls this.
    /// </summary>
    public RoleScript ForAnyUnscriptedRole()
    {
        _anyRoleFallback = new RoleScript();
        return _anyRoleFallback;
    }

    /// <inheritdoc />
    public bool IsAvailable(AIAgentFrameworkClientType clientType) => true;

    /// <inheritdoc />
    public Task<IChatClient> GetChatClientAsync(
        AIAgentFrameworkClientType clientType,
        string deploymentOrAgentId,
        CancellationToken cancellationToken = default)
    {
        var agentId = executionContext.AgentId;
        var script = ResolveScript(agentId);
        return Task.FromResult<IChatClient>(new RecordingChatClient(agentId, script, log));
    }

    /// <inheritdoc />
    /// <remarks>The fake has no SDK retry to disable, so this yields the same client.</remarks>
    public Task<IChatClient> GetChatClientWithoutProviderRetryAsync(
        AIAgentFrameworkClientType clientType,
        string deploymentOrAgentId,
        CancellationToken cancellationToken = default)
        => GetChatClientAsync(clientType, deploymentOrAgentId, cancellationToken);

    /// <inheritdoc />
    public IReadOnlyDictionary<AIAgentFrameworkClientType, bool> GetAvailableProviders() =>
        new Dictionary<AIAgentFrameworkClientType, bool> { [AIAgentFrameworkClientType.AzureOpenAI] = true };

    /// <inheritdoc />
    public AiProviderStatus GetProviderStatus() =>
        new(AIAgentFrameworkClientType.AzureOpenAI, "fake-deployment", IsConfigured: true, MissingSettings: []);

    /// <inheritdoc />
    public Task<string> CreatePersistentAgentAsync(
        string model, string name, string? instructions = null,
        string? description = null, CancellationToken cancellationToken = default)
        => Task.FromResult($"fake-agent-{Guid.NewGuid():N}");

    private RoleScript ResolveScript(string? agentId)
    {
        if (agentId is not null && _roles.TryGetValue(agentId, out var script)) return script;
        if (_anyRoleFallback is not null) return _anyRoleFallback;

        var known = _roles.Count == 0 ? "(none registered)" : string.Join(", ", _roles.Keys);
        throw new InvalidOperationException(
            $"ScriptedChatClientFactory has no script for agent id '{agentId ?? "<null>"}'. " +
            $"Registered roles: {known}. Call ForRole(agentId) to script this agent, or " +
            $"{nameof(ForAnyUnscriptedRole)}() to accept any unmatched role.");
    }
}
