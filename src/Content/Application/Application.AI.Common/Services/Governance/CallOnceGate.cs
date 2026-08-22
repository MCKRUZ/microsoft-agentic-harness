using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Governance;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Services.Governance;

/// <summary>
/// Default <see cref="ICallOnceGate"/>: consults <see cref="IToolCallOncePolicy"/> for whether
/// the tool opted in, and <see cref="IToolCallLedger"/> for whether it already ran.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Reads <see cref="IAgentExecutionContext.CallOnceScopeId"/>, never
/// <see cref="IAgentExecutionContext.ConversationId"/> directly.</strong> The latter already means
/// three different things to three different callers — a durable conversation for an agent turn, a
/// fresh-per-call value on the direct-invoke surface, and a value deliberately SHARED across every
/// run of one workflow on the plan-run surface — so reading it here would inherit whichever meaning
/// happened to be in scope. <see cref="IAgentExecutionContext.CallOnceScopeId"/> exists precisely
/// so this gate has a scope stated on its own terms by each caller, correct or absent, never
/// silently reinterpreted. See that property's remarks for the full reasoning and its two failure
/// modes if this gate ever reverts to reading <see cref="IAgentExecutionContext.ConversationId"/>
/// directly.
/// </para>
/// <para>
/// <strong>An absent scope fails OPEN, not closed — and this is not the same mistake as reading
/// an absent identity as "global" (see <c>PlannerScopeFilter</c>,
/// <c>TenantIsolatedGraphStore</c>).</strong> Those are access-control decisions: getting them
/// wrong widens who can read data that was never theirs. This is a refusal-producing control —
/// see <see cref="IToolCallLedger"/>'s remarks — and "has this tool already run in this scope" is
/// not merely unknown without one, it is undefined: a Direct API invocation genuinely has no
/// conversation for a second call to happen within, so there is nothing this gate could be
/// protecting against. Failing closed here would not narrow an access grant; it would make every
/// call-once tool permanently unusable from every surface with no call-once-meaningful scope,
/// which is a different and disproportionate restriction a SKILL.md author declaring the tool
/// call-once did not ask for.
/// </para>
/// <para>
/// <strong><see cref="IToolCallLedger"/> is optional, unlike every other collaborator here.</strong>
/// It is registered only by <c>Infrastructure.AI</c> (<c>RegisterGovernanceStateServices</c>) —
/// this Application-layer type cannot reference that project to provide its own default, the way
/// <c>Infrastructure.AI</c> can register <c>NullToolCallLedger</c>. A host that composes
/// <c>Application.AI.Common</c> alone (most test fixtures; a template consumer who has not yet
/// wired the durable governance store) would otherwise fail to resolve this whole gate, and with
/// it the entire admission chain — turning "call-once enforcement is off" into "every tool call is
/// refused," which is backwards for an opt-in feature. Absent, this behaves exactly as
/// <c>NullToolCallLedger</c> would: every call-once tool is declared but unenforced.
/// </para>
/// </remarks>
public sealed class CallOnceGate : ICallOnceGate
{
    private readonly IToolCallOncePolicy _policy;
    private readonly IToolCallLedger? _ledger;
    private readonly IAgentExecutionContext _executionContext;
    private readonly ILogger<CallOnceGate> _logger;

    /// <summary>Initializes a new instance.</summary>
    /// <param name="ledger">
    /// The durable claim store, or <see langword="null"/> when <c>Infrastructure.AI</c>'s
    /// governance-state registration is not composed — see the remarks on this type for why that
    /// must mean "unenforced," not "fail to resolve."
    /// </param>
    public CallOnceGate(
        IToolCallOncePolicy policy,
        IAgentExecutionContext executionContext,
        ILogger<CallOnceGate> logger,
        IToolCallLedger? ledger = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(executionContext);
        ArgumentNullException.ThrowIfNull(logger);

        _policy = policy;
        _executionContext = executionContext;
        _logger = logger;
        _ledger = ledger;
    }

    /// <inheritdoc />
    public async ValueTask<ToolInvocationDecision> EvaluateAsync(string toolName, CancellationToken cancellationToken)
    {
        if (!_policy.IsCallOnce(toolName))
            return ToolInvocationDecision.Allow();

        if (_ledger is null)
            return ToolInvocationDecision.Allow();

        var scopeId = _executionContext.CallOnceScopeId;
        // IsNullOrWhiteSpace, not IsNullOrEmpty: IToolCallLedger.TryClaimAsync asserts
        // ArgumentException.ThrowIfNullOrWhiteSpace on this same value, so a whitespace-only scope id
        // must be treated as absent here too — otherwise it passes this guard and TryClaimAsync throws
        // an unhandled ArgumentException out of the admission pipeline on every call-once tool call for
        // that execution, rather than the fail-open Allow() an absent scope is supposed to produce.
        if (string.IsNullOrWhiteSpace(scopeId))
            return ToolInvocationDecision.Allow();

        var claimed = await _ledger.TryClaimAsync(scopeId, toolName, cancellationToken).ConfigureAwait(false);
        if (claimed)
            return ToolInvocationDecision.Allow();

        _logger.LogInformation(
            "Refused repeat call to call-once tool {ToolName} in scope {CallOnceScopeId}.",
            toolName, scopeId);

        // Specific enough for the model to act on, mirroring the loop guard's halt message rather
        // than the generic access-control denial (GovernanceDenials.NotPermitted) every other gate
        // in this chain uses — this is not an access question, and the model needs to know not to
        // retry, not just that it was refused. Deliberately does not assert "you already have a
        // result" as fact: TryClaimAsync also returns false on a write failure it could not
        // distinguish from a genuine duplicate (see EfCoreToolCallLedger's remarks), and a message
        // claiming a result exists when the tool may never have run would invite the model to
        // fabricate one rather than surface the refusal honestly.
        return ToolInvocationDecision.Deny(
            $"Tool '{toolName}' may be called at most once per conversation and cannot be called "
            + "again right now. Do not retry it — if you need its result, use what you already have "
            + "or report that it is unavailable.");
    }
}
