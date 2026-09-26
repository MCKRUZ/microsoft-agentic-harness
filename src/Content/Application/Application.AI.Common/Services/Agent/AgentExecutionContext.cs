using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Telemetry;
using Application.AI.Common.Services.Telemetry;
using Domain.AI.Identity;

namespace Application.AI.Common.Services.Agent;

/// <summary>
/// Scoped ambient context carrying the identity of the currently executing agent.
/// Set once per request by <see cref="Application.AI.Common.MediatRBehaviors.AgentContextPropagationBehavior{TRequest, TResponse}"/>
/// and consumed by downstream behaviors, handlers, and services.
/// </summary>
/// <remarks>
/// <para>
/// Registered as <c>Scoped</c> in DI — each MediatR request scope gets its own instance.
/// Properties remain <c>null</c> for non-agent requests.
/// </para>
/// <para>
/// <strong>This type also publishes the turn's external governance attribution</strong>
/// (<see cref="IAgentTelemetryAttribution"/>), on <see cref="Initialize"/>, released when the DI scope
/// that owns this instance is disposed. That is deliberate rather than incidental: publishing "which
/// agent, in which conversation" to an external control plane is a direct consequence of holding those
/// values, and tying the publication to this object's lifetime is what makes it impossible for a call
/// site to establish the context and forget the attribution. It previously sat next to
/// <see cref="Initialize"/> as a separate per-site ritual and was missed at three of five call sites —
/// see <see cref="Initialize"/>'s remarks (#737).
/// </para>
/// </remarks>
public sealed class AgentExecutionContext : IAgentExecutionContext, IDisposable
{
    // Single gate for both Initialize and SetIdentity so the interface's
    // documented thread-safety contract holds: "multiple concurrent agent
    // requests may execute within overlapping async contexts." Without
    // locking, check-then-set on _initialized / AgentIdentity is a TOCTOU
    // window in which two writers with different values both pass the check
    // and the last writer silently wins.
    private readonly object _gate = new();
    private readonly IAgentTelemetryAttribution _attribution;
    private bool _initialized;

    // The current turn's published attribution, held so it stays in effect while that turn's spans are
    // created. Null until the first Initialize, then replaced on each subsequent one — see Initialize
    // for why a later turn must republish rather than inherit.
    private IDisposable? _attributionScope;
    private bool _disposed;

    // Computed once, at construction — before Initialize is ever called, and independent of
    // whatever it's later called with. This scope must exist and be stable even for a caller
    // that never calls Initialize at all (nothing today does that, but ToolResultScopeId itself
    // must not throw or return null just because initialization hasn't happened yet).
    private readonly string _fallbackToolResultScopeId = Guid.NewGuid().ToString("N");

    // Freezes ToolResultScopeId's answer on first read (#562). Without this, a read before
    // Initialize() and a read after it can observe two different values — CallOnceScopeId ??
    // _fallbackToolResultScopeId flips the moment Initialize supplies a non-null scope — and
    // anything spilled under the first value becomes permanently unfindable under the second,
    // silently: the tool-result store makes "wrong scope" indistinguishable from "never existed".
    // Not reachable today (every Initialize call site runs before any tool call), but nothing
    // enforced that ordering; this makes the guarantee explicit instead of incidental.
    private string? _observedToolResultScopeId;

    /// <summary>
    /// Initializes a new instance of the <see cref="AgentExecutionContext"/> class with no attribution
    /// integration.
    /// </summary>
    /// <remarks>
    /// For a caller that only needs identity/turn-tracking and has no reason to care about external
    /// governance attribution — chiefly direct construction in tests. Delegates to the benign
    /// <see cref="NoOpAgentTelemetryAttribution"/> singleton rather than leaving <c>_attribution</c>
    /// nullable, so <see cref="Initialize"/> never has to branch on whether one was supplied.
    /// </remarks>
    public AgentExecutionContext() : this(NoOpAgentTelemetryAttribution.Instance)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AgentExecutionContext"/> class.
    /// </summary>
    /// <param name="attribution">
    /// Publishes the turn's identity to an external agent-governance platform. Non-null by contract:
    /// a caller that has none should use the parameterless constructor rather than pass a null here, so
    /// this type never silently degrades an intended integration into no attribution at all. Production
    /// resolves this constructor through DI, where <c>AddApplicationAiCommonDependencies</c> registers
    /// the same no-op as the unconditional default — so a host that has not opted into an integration
    /// still reaches this constructor, not the parameterless one; the parameterless one exists purely
    /// for callers outside DI.
    /// </param>
    public AgentExecutionContext(IAgentTelemetryAttribution attribution)
    {
        ArgumentNullException.ThrowIfNull(attribution);
        _attribution = attribution;
    }

    /// <inheritdoc />
    public string? AgentId { get; private set; }

    /// <inheritdoc />
    public string? ConversationId { get; private set; }

    /// <inheritdoc />
    public int? TurnNumber { get; private set; }

    /// <inheritdoc />
    public string? CallOnceScopeId { get; private set; }

    /// <inheritdoc />
    public string ToolResultScopeId
    {
        get
        {
            lock (_gate)
            {
                return _observedToolResultScopeId ??= CallOnceScopeId ?? _fallbackToolResultScopeId;
            }
        }
    }

    /// <inheritdoc />
    public bool HasRetrievableToolResultScope
    {
        get
        {
            // Correctness-review advisory: read under the same _gate as ToolResultScopeId and
            // Initialize, not bare — CallOnceScopeId is written inside Initialize's lock, and a
            // lock-free read here would be the one place in this type that doesn't honor its own
            // documented "overlapping async contexts" thread-safety contract.
            lock (_gate)
            {
                return CallOnceScopeId is not null;
            }
        }
    }

    /// <inheritdoc />
    public AgentIdentity? AgentIdentity { get; private set; }

    /// <inheritdoc />
    public void Initialize(string agentId, string conversationId, int turnNumber, string? callOnceScopeId = null)
    {
        lock (_gate)
        {
            // Guard against scope leak: re-initialization with a different agent, conversation, or
            // call-once scope within the same DI scope is always a bug. Only turn number may change
            // (subsequent turns).
            if (_initialized && (AgentId != agentId || ConversationId != conversationId
                || CallOnceScopeId != callOnceScopeId))
                throw new InvalidOperationException(
                    $"AgentExecutionContext scope conflict: already bound to agent '{AgentId}' / " +
                    $"conversation '{ConversationId}' / call-once scope '{CallOnceScopeId}', cannot " +
                    $"re-initialize with agent '{agentId}' / conversation '{conversationId}' / " +
                    $"call-once scope '{callOnceScopeId}'.");

            // ToolResultScopeId was already read (and, per the guard above, this is either the
            // first Initialize call or a value-identical re-initialize) — if the scope id it
            // observed no longer matches what CallOnceScopeId is about to become, that observer
            // is now holding a stale scope. Fail loudly rather than let a spill become orphaned.
            if (_observedToolResultScopeId is not null
                && _observedToolResultScopeId != (callOnceScopeId ?? _fallbackToolResultScopeId))
                throw new InvalidOperationException(
                    $"AgentExecutionContext scope conflict: ToolResultScopeId was already read as " +
                    $"'{_observedToolResultScopeId}' before Initialize supplied call-once scope " +
                    $"'{callOnceScopeId}'. ToolResultScopeId must not be read before Initialize().");

            AgentId = agentId;
            ConversationId = conversationId;
            TurnNumber = turnNumber;
            CallOnceScopeId = callOnceScopeId;
            _initialized = true;

            // Deliberately inside the lock, unlike Dispose's release — considered and rejected splitting
            // it out (correctness-review advisory during #737's fix-up pass). BeginTurn IS a public
            // extensibility point and a slow implementation would hold _gate longer than the read-only
            // properties below would like. But release-then-publish-then-assign has to be atomic against
            // a second Initialize on the same instance with the SAME agent/conversation (a legitimate,
            // tested case — turn 2 of a multi-turn conversation): splitting the critical section lets two
            // such calls interleave their BeginTurn/assign halves and overwrite each other's scope without
            // releasing it, a genuine leak in exchange for an unmeasured, currently-hypothetical win — no
            // shipped implementation of IAgentTelemetryAttribution does anything slower than a dictionary
            // lookup and a Baggage write. If a real implementation is ever measured to be slow here, the
            // fix is to make ITS OWN wrapped work fast or cached (see Agent365TelemetryAttribution's
            // per-agent identity cache, added for exactly this), not to loosen this lock.
            //
            // Republished on EVERY call, not only the first, even though the two values it carries
            // cannot have changed (the guard above rejects any change to agent or conversation).
            // Attribution is ambient to the async flow that publishes it, and one DI scope serves
            // several turns — a conversation dispatches each turn as a sibling send within the scope it
            // owns, calling this once per turn. Publishing only on the first turn leaves every later
            // turn depending on the first turn's values still being reachable from a sibling flow,
            // which holds or not according to where awaits happen to fall between the dispatch and the
            // publish. Turns 2+ then export spans with no agent id, which a governance platform
            // discards WITHOUT reporting an error — a conversation would appear in the tenant's records
            // with its first turn only. See MultiTurnAttributionTests, which measures this against the
            // real SDK; the first cut of #737 published once and the correctness gate caught it.
            //
            // The previous turn's scope is released first, so exactly one is ever live and the count
            // does not grow with conversation length.
            //
            // Nothing is published once disposed. Nothing reaches a disposed scoped service today, but
            // publishing there would create a scope with nothing left to release it — the one shape of
            // leak this design would otherwise introduce.
            if (!_disposed)
            {
                _attributionScope?.Dispose();
                _attributionScope = _attribution.BeginTurn(agentId, conversationId);
            }
        }
    }

    /// <summary>
    /// Releases the turn's external governance attribution.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called by the DI container when the scope owning this instance is disposed — which is what makes
    /// attribution automatic rather than a per-call-site ritual. Three of the five initializing paths
    /// create that scope themselves and dispose it in the same method
    /// (<c>DirectToolInvoker</c>, <c>PlanRunExecutor</c>, <c>SubPlanStepExecutor</c>); the other two
    /// (<c>AgentContextPropagationBehavior</c> and <c>RunOrchestratedTaskCommandHandler</c>) are handed
    /// the ambient request-scoped context and rely on whoever opened that request scope to dispose it,
    /// which every dispatcher does. So no path publishes attribution without something releasing it.
    /// </para>
    /// <para>
    /// The release is not guaranteed to run on the flow that published, and it does not need to.
    /// Attribution is ambient per async flow: where the publish created that ambient state itself, it
    /// dies with the turn's own flow regardless; where it mutated ambient state an enclosing flow already
    /// owned, that state is shared, so releasing it from anywhere still clears it. The observable cost is
    /// that attribution can outlast the turn by the remainder of the request — same agent and tenant, so
    /// an accuracy cost rather than a disclosure one. Pinned by
    /// <c>AttributionOutlivesTheTurnButNotTheScope</c>.
    /// </para>
    /// <para>
    /// Idempotent. A caller that disposes this directly as well as letting the container dispose it
    /// must not release the underlying scope twice.
    /// </para>
    /// </remarks>
    public void Dispose()
    {
        IDisposable? scope;

        lock (_gate)
        {
            // Taking the scope and clearing the field in one locked step is what makes this idempotent:
            // a second call — or a concurrent one — finds null and releases nothing. An additional
            // "already disposed, return early" check would be dead weight; mutation-testing this method
            // confirmed removing such a check changed no behaviour.
            _disposed = true;
            scope = _attributionScope;
            _attributionScope = null;
        }

        // Disposed outside the lock: releasing the scope is another component's code, and holding this
        // type's gate across a call into it would make the lock's span depend on that component.
        scope?.Dispose();
    }

    /// <inheritdoc />
    public void SetIdentity(AgentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        lock (_gate)
        {
            // Same scope-leak guard as Initialize, applied to identity. Re-setting a
            // value-equal identity short-circuits to a literal no-op so the documented
            // idempotent contract holds without a redundant assignment in the setter.
            if (AgentIdentity is not null)
            {
                if (AgentIdentity.Equals(identity))
                    return;

                throw new InvalidOperationException(
                    $"AgentExecutionContext identity conflict: already bound to identity " +
                    $"'{AgentIdentity.Id}' (kind {AgentIdentity.Kind}), cannot re-bind to " +
                    $"identity '{identity.Id}' (kind {identity.Kind}).");
            }

            AgentIdentity = identity;
        }
    }
}
