using Application.AI.Common.Interfaces.Telemetry;
using Domain.Common.Config;
using Domain.Common.Config.Observability;
using Domain.Common.Helpers;
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

    // Set once the misconfiguration has been reported, so it produces one line rather than one per turn
    // forever. A rejected wildcard grant logging on every single tool call is a live defect in this
    // repo already; this avoids repeating that shape on the turn path.
    private int _warned;

    // The tenant id is host-level, not per-agent, and is exactly as immutable as _config — recomputed
    // per call for no reason. Null when the config makes tenant attribution impossible (blank), which
    // BeginTurn treats identically to today's inline check.
    private readonly string? _canonicalTenantId;

    // BeginTurn is on the hot path for every turn AND every direct tool invocation/plan step/sub-plan step
    // (#737 moved publication into AgentExecutionContext.Initialize, which those three previously never
    // called into at all). Every value an agent's identity resolves to is a pure function of THIS host's
    // own config, so the first cut of this precompute cached the resolution keyed by the caller-supplied
    // agentId instead — found by security review to be a real problem, not a style one: an authenticated
    // caller (or any caller, on a host running without auth) can create a conversation under a fresh,
    // never-before-seen agent name per call, and each one left a permanent, never-evicted cache entry in
    // this process-lifetime singleton — an unbounded memory-growth surface with no cap and no metric. It
    // also cached each agent's DISPLAY NAME keyed by whichever caller happened to ask first, so a caller
    // who spelled a real agent's id in a different casing (accepted, because Agents matches
    // case-insensitively) would have that casing served to every later, legitimate caller of the same
    // agent — a cross-caller integrity bug in the tenant's own governance record, the one thing this
    // whole file exists to keep accurate.
    //
    // Correct fix: precompute from config alone, not from anything a caller supplies. The size of these
    // two fields is bounded by how many agents THIS HOST configured, never by how many distinct agent ids
    // a caller has asked about. The display name is deliberately NOT stored here — it depends on the
    // CALLING agentId's own spelling (see BeginTurn), which is exactly the value that must not be cached
    // across callers.
    private readonly ResolvedIdentity? _hostDefault;
    private readonly Dictionary<string, ResolvedIdentity> _overrides;

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

        // Host-level, not per-agent — computed once here rather than on every BeginTurn call. Left null
        // when blank, which BeginTurn treats exactly as the inline check it replaces did.
        _canonicalTenantId = string.IsNullOrWhiteSpace(_config.TenantId)
            ? null
            : GuidId.Canonicalize(_config.TenantId);

        _hostDefault = string.IsNullOrWhiteSpace(_config.AgentAppId)
            ? null
            : new ResolvedIdentity(
                GuidId.Canonicalize(_config.AgentAppId)!,
                CanonicalizeBlueprint(_config.BlueprintId));

        // Same comparer Agents itself enforces in its setter — one lookup here has to agree with the one
        // FindOverride used to do, or an agent id that matched there would silently stop matching here.
        _overrides = new Dictionary<string, ResolvedIdentity>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, entry) in _config.Agents)
        {
            if (!string.IsNullOrWhiteSpace(entry.AppId))
            {
                _overrides[name] = new ResolvedIdentity(
                    GuidId.Canonicalize(entry.AppId)!,
                    CanonicalizeBlueprint(entry.BlueprintId));
            }
        }
    }

    // Blank-checked, not null-checked, to match what the validator accepts: it treats a blank blueprint id
    // as absent so a copied template placeholder does not refuse a boot. A null check here would let that
    // blank through and publish an empty blueprint rather than omitting it.
    private static string? CanonicalizeBlueprint(string? blueprintId)
        => string.IsNullOrWhiteSpace(blueprintId) ? null : GuidId.Canonicalize(blueprintId);

    /// <inheritdoc />
    public IDisposable BeginTurn(string agentId, string conversationId)
    {
        var config = _config;

        if (!config.Enabled)
        {
            return NoAgentTelemetryAttributionScope.Instance;
        }

        var identity = ResolveIdentity(agentId, out var isHostDefault);
        if (identity is null)
        {
            WarnOnce(agentId);
            return NoAgentTelemetryAttributionScope.Instance;
        }

        // The tenant half gets the same treatment as the agent half, for the same reason: an attribution
        // naming an agent but no tenant is dropped by the service just as surely as the reverse, so
        // publishing a partial identity buys nothing and hides the misconfiguration. This guard was
        // missing while every other value here was blank-checked — an unintended asymmetry, not a
        // decision.
        if (_canonicalTenantId is null)
        {
            WarnOnce(agentId);
            return NoAgentTelemetryAttributionScope.Instance;
        }

        // The host-level AgentName names the host's default agent, so it must not be applied to an agent
        // reporting its own identity: doing so collapses every agent in a multi-agent host to one display
        // name while their ids stay distinct, which is harder to read in the tenant's inventory than no
        // custom name at all. Blank-checked for the same reason as the blueprint id: a copied template
        // placeholder ("AgentName": "") means "not provided".
        //
        // Resolved from THIS call's own agentId every time, deliberately never cached: caching it against
        // whichever caller asked first would serve that caller's exact casing to every later, distinct
        // caller of the same agent — a cross-caller integrity bug in the tenant's own governance record,
        // found by security review on the first cut of this cache.
        var agentName = isHostDefault && !string.IsNullOrWhiteSpace(config.AgentName)
            ? config.AgentName
            : agentId;

        var builder = new BaggageBuilder()
            .TenantId(_canonicalTenantId)
            .AgentId(identity.Value.CanonicalAppId)
            .AgentName(agentName);

        if (identity.Value.CanonicalBlueprintId is not null)
        {
            builder = builder.AgentBlueprintId(identity.Value.CanonicalBlueprintId);
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
    /// The canonicalized form of one configured identity: a blueprint's kind aside, everything Agent 365
    /// needs to attribute a span to it. Precomputed once from config, never from a caller's input — see
    /// <see cref="_hostDefault"/> and <see cref="_overrides"/> for why.
    /// </summary>
    private readonly record struct ResolvedIdentity(string CanonicalAppId, string? CanonicalBlueprintId);

    /// <summary>
    /// Selects the Entra agent identity for <paramref name="agentId"/> — its own override when one is
    /// configured, otherwise the host-level default.
    /// </summary>
    /// <remarks>
    /// An override supplies its own blueprint or none — the host-level blueprint is deliberately not
    /// inherited, because a blueprint identifies a <em>kind</em> of agent and an agent minted from a
    /// different blueprint would otherwise be filed under the wrong kind.
    /// </remarks>
    private ResolvedIdentity? ResolveIdentity(string agentId, out bool isHostDefault)
    {
        if (!string.IsNullOrWhiteSpace(agentId) && _overrides.TryGetValue(agentId, out var over))
        {
            isHostDefault = false;
            return over;
        }

        isHostDefault = true;
        return _hostDefault;
    }

    private void WarnOnce(string agentId)
    {
        // One flag, not a set keyed by agent. Both conditions that reach here — no configured agent id,
        // and a blank tenant — are host-wide, so no configuration produces this for one agent and not
        // another. Keying per agent would turn a single host misconfiguration into one warning per
        // distinct agent name, which is the opposite of what deduplicating it is for.
        if (Interlocked.Exchange(ref _warned, 1) != 0)
        {
            return;
        }

        _logger.LogWarning(
            "Agent 365 export is enabled but no agent identity is configured for agent {AgentId}, so "
            + "this agent's activity will not reach the tenant's agent control plane. Set "
            + "Observability:Exporters:Agent365:AgentAppId, or add an entry for this agent under "
            + "Observability:Exporters:Agent365:Agents.",
            agentId);
    }
}
