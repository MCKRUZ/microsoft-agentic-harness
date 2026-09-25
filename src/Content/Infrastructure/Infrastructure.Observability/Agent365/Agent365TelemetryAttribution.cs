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
/// A turn whose agent has no configured identity publishes nothing rather than falling back to some
/// other identity. Attributing one agent's activity to another agent's identity in a tenant's
/// governance records is worse than leaving it unattributed.
/// </para>
/// <para>
/// That branch should be unreachable in practice: <c>Agent365ExporterConfigValidator</c> is bound with
/// <c>ValidateOnStart</c> and refuses a host whose <c>AgentAppId</c> is not a GUID while the exporter
/// is enabled, so a booted host always has a host-level identity to fall back to. It is retained as
/// defence in depth — the validator binds the config section on its own, while this reads the same
/// values out of the <c>AppConfig</c> tree, so the guarantee is a consequence of both binding the same
/// section rather than something the type system enforces. The warning is what makes the unreachable
/// case visible if that ever stops being true.
/// </para>
/// </remarks>
public sealed class Agent365TelemetryAttribution : IAgentTelemetryAttribution
{
    // Captured once rather than re-read per turn, deliberately. The exporter is wired into the
    // OpenTelemetry pipeline at composition time and the startup validator runs once, so both are
    // startup decisions. Re-reading the flag here would let the two diverge: switching Enabled on by
    // a live config reload, in a host that was not wired at boot, would publish attribution for every
    // turn while nothing exported it — and no error anywhere, which is precisely the silent failure
    // the startup validator exists to prevent. One snapshot means one decision. It also closes the
    // narrower case of a reload clearing TenantId while Enabled stays true.
    private readonly Agent365ExporterConfig _config;
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
        _config = config.CurrentValue.Observability.Exporters.Agent365;
        _logger = logger;
    }

    /// <inheritdoc />
    public IDisposable BeginTurn(string agentId, string conversationId)
    {
        var config = _config;

        if (!config.Enabled)
        {
            return NoAgentTelemetryAttributionScope.Instance;
        }

        var identity = ResolveIdentity(config, agentId);
        if (identity is null)
        {
            return NoAgentTelemetryAttributionScope.Instance;
        }

        // The host-level AgentName names the host's default agent, so it must not be applied to an
        // agent reporting its own identity: doing so collapses every agent in a multi-agent host to
        // one display name while their ids stay distinct, which is harder to read in the tenant's
        // inventory than no custom name at all.
        var agentName = identity.Value.IsHostDefault
            ? config.AgentName ?? agentId
            : agentId;

        var builder = new BaggageBuilder()
            .TenantId(config.TenantId)
            .AgentId(identity.Value.AppId)
            .AgentName(agentName);

        // Blank-checked, not null-checked, to match what the validator now accepts: it treats a blank
        // blueprint id as absent so a copied template placeholder does not refuse a boot. A null check
        // here would let that blank through and publish an empty blueprint rather than omitting it.
        if (!string.IsNullOrWhiteSpace(identity.Value.BlueprintId))
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
    private (string AppId, string? BlueprintId, bool IsHostDefault)? ResolveIdentity(
        Agent365ExporterConfig config,
        string agentId)
    {
        var over = FindOverride(config, agentId);
        if (!string.IsNullOrWhiteSpace(over?.AppId))
        {
            return (over.AppId, over.BlueprintId, false);
        }

        if (!string.IsNullOrWhiteSpace(config.AgentAppId))
        {
            return (config.AgentAppId, config.BlueprintId, true);
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
}
