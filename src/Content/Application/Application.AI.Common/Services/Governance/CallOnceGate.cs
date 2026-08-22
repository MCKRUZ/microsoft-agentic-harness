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
/// <strong>An absent conversation id fails OPEN, not closed — and this is not the same mistake
/// as reading an absent identity as "global" (see <c>PlannerScopeFilter</c>,
/// <c>TenantIsolatedGraphStore</c>).</strong> Those are access-control decisions: getting them
/// wrong widens who can read data that was never theirs. This is a refusal-producing control —
/// see <see cref="IToolCallLedger"/>'s remarks — and "has this tool already run in this
/// conversation" is not merely unknown without a conversation id, it is undefined: a Direct API
/// invocation or a plan step with no conversation id has no conversation for a second call to
/// happen within, so there is nothing this gate could be protecting against. Failing closed here
/// would not narrow an access grant; it would make every call-once tool permanently unusable
/// from every non-conversational surface, which is a different and disproportionate restriction
/// a SKILL.md author declaring the tool call-once did not ask for.
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

        var conversationId = _executionContext.ConversationId;
        if (string.IsNullOrEmpty(conversationId))
            return ToolInvocationDecision.Allow();

        var claimed = await _ledger.TryClaimAsync(conversationId, toolName, cancellationToken).ConfigureAwait(false);
        if (claimed)
            return ToolInvocationDecision.Allow();

        _logger.LogInformation(
            "Refused repeat call to call-once tool {ToolName} in conversation {ConversationId}.",
            toolName, conversationId);

        // Specific enough for the model to act on, mirroring the loop guard's halt message rather
        // than the generic access-control denial (GovernanceDenials.NotPermitted) every other gate
        // in this chain uses — this is not an access question, and the model needs to know not to
        // retry, not just that it was refused.
        return ToolInvocationDecision.Deny(
            $"Tool '{toolName}' may be called at most once per conversation and has already been called. "
            + "Do not call it again; use the result you already have.");
    }
}
