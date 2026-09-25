using Application.AI.Common.Interfaces.Telemetry;
using Domain.Common.Config;
using Domain.Common.Config.Observability;
using Microsoft.Agents.A365.Observability.Runtime.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Observability.Agent365;

/// <summary>
/// Publishes the running agent's Microsoft Agent 365 identity for the duration of a turn, so the
/// Agent 365 exporter can attribute the turn's spans to a real Entra agent identity.
/// </summary>
/// <remarks>
/// <para>
/// This is the piece the whole integration rests on. Agent 365 identifies every span by agent id and
/// tenant id and <strong>silently drops</strong> any span carrying neither — no error, no rejected
/// count, the agent simply never appears. The values are published as OpenTelemetry baggage here, at
/// the start of the turn, and the vendor's own span processor copies them onto each span as it starts.
/// </para>
/// <para>
/// Reading them at export time instead would not work: an exporter runs on a background batching
/// thread where ambient context is empty. That is why this is a turn-boundary concern rather than an
/// exporter concern.
/// </para>
/// <para>
/// A turn whose agent has no configured identity publishes nothing and says so once per agent, rather
/// than publishing the host default. Attributing one agent's activity to another agent's identity in
/// a tenant's governance records is worse than leaving it unattributed.
/// </para>
/// </remarks>
public sealed class Agent365TelemetryAttribution : IAgentTelemetryAttribution
{
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly ILogger<Agent365TelemetryAttribution> _logger;

    // Agents already warned about, so a misconfigured agent produces one line rather than one per
    // turn forever. A rejected wildcard grant logging on every single tool call is a live defect in
    // this repo already; this avoids repeating that shape on the turn path.
    private readonly HashSet<string> _warnedAgents = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="Agent365TelemetryAttribution"/> class.
    /// </summary>
    public Agent365TelemetryAttribution(
        IOptionsMonitor<AppConfig> config,
        ILogger<Agent365TelemetryAttribution> logger)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc />
    public IDisposable BeginTurn(string agentId, string conversationId)
    {
        var config = _config.CurrentValue.Observability.Exporters.Agent365;
        if (!config.Enabled)
        {
            return NullScope.Instance;
        }

        var identity = ResolveIdentity(config, agentId);
        if (identity is null)
        {
            return NullScope.Instance;
        }

        var builder = new BaggageBuilder()
            .TenantId(config.TenantId)
            .AgentId(identity.Value.AppId)
            .AgentName(config.AgentName ?? agentId);

        if (identity.Value.BlueprintId is not null)
        {
            builder = builder.AgentBlueprintId(identity.Value.BlueprintId);
        }

        // Conversation id is Agent 365's primary join key for grouping a run's spans into a session.
        // Guarded because an empty value would publish an empty join key rather than omitting it.
        if (!string.IsNullOrWhiteSpace(conversationId))
        {
            builder = builder.ConversationId(conversationId);
        }

        return builder.Build();
    }

    /// <summary>
    /// Selects the Entra agent identity for <paramref name="agentId"/>: its own override when one is
    /// configured, otherwise the host-level default.
    /// </summary>
    /// <remarks>
    /// An override supplies its own blueprint or none — the host-level blueprint is deliberately not
    /// inherited, because a blueprint identifies a <em>kind</em> of agent and an agent minted from a
    /// different blueprint would otherwise be filed under the wrong kind.
    /// </remarks>
    private (string AppId, string? BlueprintId)? ResolveIdentity(
        Agent365ExporterConfig config,
        string agentId)
    {
        var over = FindOverride(config, agentId);
        if (!string.IsNullOrWhiteSpace(over?.AppId))
        {
            return (over.AppId, over.BlueprintId);
        }

        if (!string.IsNullOrWhiteSpace(config.AgentAppId))
        {
            return (config.AgentAppId, config.BlueprintId);
        }

        WarnOnce(agentId);
        return null;
    }

    /// <summary>
    /// Finds the override for <paramref name="agentId"/>, matching the configured key
    /// case-insensitively.
    /// </summary>
    /// <remarks>
    /// The comparison is done here rather than by giving the dictionary an
    /// <see cref="StringComparer.OrdinalIgnoreCase"/> comparer, because configuration binding
    /// populates this property by assigning a dictionary of its own making — a comparer set on the
    /// initializer does not survive binding, so relying on it would work in a unit test and fail
    /// against real configuration. Keys are operator-typed agent names; matching them
    /// case-sensitively would fall through to the host default silently, attributing the agent's
    /// activity to the wrong identity.
    /// </remarks>
    private static Agent365AgentIdentityConfig? FindOverride(
        Agent365ExporterConfig config,
        string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId) || config.Agents.Count == 0)
        {
            return null;
        }

        if (config.Agents.TryGetValue(agentId, out var exact))
        {
            return exact;
        }

        foreach (var (name, identity) in config.Agents)
        {
            if (string.Equals(name, agentId, StringComparison.OrdinalIgnoreCase))
            {
                return identity;
            }
        }

        return null;
    }

    private void WarnOnce(string agentId)
    {
        lock (_warnedAgents)
        {
            if (!_warnedAgents.Add(agentId ?? string.Empty))
            {
                return;
            }
        }

        _logger.LogWarning(
            "Agent 365 export is enabled but no agent identity is configured for agent {AgentId}, so "
            + "this agent's activity will not reach the tenant's agent control plane. Set "
            + "Observability:Exporters:Agent365:AgentAppId, or add an entry for this agent under "
            + "Observability:Exporters:Agent365:Agents.",
            agentId);
    }

    /// <summary>A disposable that does nothing, for turns this attribution does not publish.</summary>
    private sealed class NullScope : IDisposable
    {
        internal static readonly NullScope Instance = new();

        private NullScope()
        {
        }

        public void Dispose()
        {
        }
    }
}
