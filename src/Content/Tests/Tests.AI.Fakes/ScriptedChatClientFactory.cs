using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Models;
using Domain.Common.Config.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Tests.AI.Fakes;

/// <summary>
/// Role-aware <see cref="IChatClientFactory"/> fake. Selects which <see cref="RoleScript"/> a call
/// gets by reading <see cref="IAgentExecutionContext.AgentId"/> — the ambient identity
/// <c>AgentContextPropagationBehavior</c> stamps before a handler runs — rather than by sniffing
/// prompt text.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Lifetime: register Singleton, matching production's real <c>IChatClientFactory</c>
/// (<c>Infrastructure.AI/DependencyInjection.cs</c>).</strong> This type does NOT constructor-inject
/// the scoped <see cref="IAgentExecutionContext"/> directly — doing so would either fail container
/// validation, or, under a hand-built test container with scope validation off, silently pin every
/// resolution to whichever scope built the factory first (role-bleed indistinguishable from a real
/// test bug). Instead it takes the singleton <see cref="IAmbientRequestScope"/> — the same
/// AsyncLocal-backed bridge production singletons already use to reach per-request scoped services
/// (see <c>KnowledgeMemoryContextProvider</c>, <c>AgentExecutionContextFactory.ContextProviders</c>)
/// — and resolves <see cref="IAgentExecutionContext"/> from <see cref="IAmbientRequestScope.Current"/>
/// at call time. A caller must have the real <c>AmbientRequestScopeBehavior&lt;,&gt;</c> (or an
/// equivalent manual <see cref="IAmbientRequestScope.BeginScope"/>) established for the duration of
/// the request, exactly as production does.
/// </para>
/// <para>
/// <strong>Fails loudly, and distinguishes why.</strong> No ambient scope established, no
/// <see cref="IAgentExecutionContext"/> registered in that scope, and an unscripted agent id are
/// three different misconfigurations and throw three differently-worded exceptions — collapsing
/// them would turn "you forgot to establish the ambient scope in your test container" into a
/// confusing "no script for id '&lt;null&gt;'" message.
/// </para>
/// </remarks>
public sealed class ScriptedChatClientFactory(IAmbientRequestScope ambientScope, ChatInvocationLog log) : IChatClientFactory
{
    private readonly Lock _rolesGate = new();
    private readonly Dictionary<string, RoleScript> _roles = new(StringComparer.Ordinal);
    private RoleScript? _anyRoleFallback;

    /// <summary>Registers (or returns the existing) script for the given agent id.</summary>
    public RoleScript ForRole(string agentId)
    {
        lock (_rolesGate)
        {
            if (_roles.TryGetValue(agentId, out var existing)) return existing;
            var script = new RoleScript();
            _roles[agentId] = script;
            return script;
        }
    }

    /// <summary>
    /// Registers a fallback script used for any call whose agent id has no dedicated
    /// <see cref="ForRole"/> script. An explicit opt-in, not a silent default — a test that wants
    /// role separation enforced simply never calls this. Idempotent: a second call returns the
    /// same script rather than discarding whatever the first call had already queued.
    /// </summary>
    public RoleScript ForAnyUnscriptedRole()
    {
        lock (_rolesGate)
        {
            _anyRoleFallback ??= new RoleScript();
            return _anyRoleFallback;
        }
    }

    /// <inheritdoc />
    public bool IsAvailable(AIAgentFrameworkClientType clientType) => true;

    /// <inheritdoc />
    public Task<IChatClient> GetChatClientAsync(
        AIAgentFrameworkClientType clientType,
        string deploymentOrAgentId,
        CancellationToken cancellationToken = default)
    {
        var agentId = ResolveAgentId();
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

    private string? ResolveAgentId()
    {
        var requestServices = ambientScope.Current
            ?? throw new InvalidOperationException(
                "ScriptedChatClientFactory could not resolve an agent id: no ambient request scope is " +
                "established. Wrap the call in IAmbientRequestScope.BeginScope(...) (or register the real " +
                "AmbientRequestScopeBehavior<,> in the MediatR pipeline) before invoking anything that " +
                "resolves IChatClientFactory.");

        var executionContext = requestServices.GetService<IAgentExecutionContext>()
            ?? throw new InvalidOperationException(
                "ScriptedChatClientFactory could not resolve an agent id: the current ambient request " +
                "scope has no IAgentExecutionContext registered. Register it (matching production's " +
                "AddScoped<IAgentExecutionContext, AgentExecutionContext>()) in the test container.");

        return executionContext.AgentId;
    }

    private RoleScript ResolveScript(string? agentId)
    {
        lock (_rolesGate)
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
}
