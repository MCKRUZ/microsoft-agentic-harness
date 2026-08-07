namespace Application.AI.Common.Interfaces.Governance;

/// <summary>
/// A consumer-authored rule that inspects a tool call in flight, immediately before it executes,
/// and can stop it or send it to a human.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists.</strong> The harness ships three fixed observers at the tool-call
/// chokepoint: <see cref="IToolInvocationGovernor"/> answers "may this agent use this tool at all",
/// <see cref="IToolClassificationGate"/> answers "is the data it touches too sensitive", and
/// <see cref="IProgressEvaluator"/> answers "is the agent stuck in a loop". None of them answers
/// "is <em>this specific invocation</em>, with <em>these arguments</em>, a good idea right now" —
/// a question only the consumer's domain can answer. "Never wire more than ten thousand", "never
/// drop a production table", "never email outside the tenant" are all rules the harness cannot
/// know. Before this seam existed there was nowhere to put them: the chokepoint class is internal
/// and sealed, so the only options were forking it or giving up.
/// </para>
/// <para>
/// <strong>It composes with admission control; it does not replace it.</strong> Observers run
/// <em>after</em> the governor, the classification gate, and the progress guard have all permitted
/// the call, so an observer only ever sees a call that was otherwise about to execute. An observer
/// therefore cannot widen access — it cannot resurrect a call the governor denied, cannot overrule
/// the capability envelope, and cannot bypass a plugin's deny list. Its verdict can only make the
/// outcome stricter than it already was.
/// </para>
/// <para>
/// <strong>Registration is the opt-in.</strong> A host that registers no observers pays nothing —
/// there is no config flag to set and no evaluation on the hot path. Register implementations in
/// the host's composition root and they are consulted, in registration order, on every agent tool
/// call.
/// </para>
/// <para>
/// <strong>Keep it fast, and expect it to be on the latency path.</strong> Every observer runs
/// inside the agent's turn while the model waits. Deterministic in-process checks are the intended
/// shape. An observer that calls a model or a remote service is possible but pays that cost on
/// every tool call, and should carry its own timeout — the chain will not impose one.
/// </para>
/// </remarks>
public interface IToolCallObserver
{
    /// <summary>
    /// A short, stable name for this observer, used in logs, audit records, and metric tags so a
    /// blocked call can be traced to the rule that blocked it.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Inspects a tool call that is about to execute and rules on it.
    /// </summary>
    /// <param name="observation">The call and the turn it belongs to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="ToolCallVerdict.Proceed"/> to let the call run, <see cref="ToolCallVerdict.Block"/>
    /// to stop it, or <see cref="ToolCallVerdict.RequireApproval"/> to suspend it pending a human
    /// decision through the configured approval workflow.
    /// </returns>
    /// <remarks>
    /// <strong>Throwing blocks the call.</strong> An observer that throws is treated as a block,
    /// not as an abstention — a rule that cannot run has not cleared the action it was there to
    /// judge. Return <see cref="ToolCallVerdict.Proceed"/> explicitly when a rule does not apply.
    /// </remarks>
    ValueTask<ToolCallVerdict> ObserveAsync(ToolCallObservation observation, CancellationToken cancellationToken);
}

/// <summary>
/// One tool call presented to an observer, together with the turn it belongs to.
/// </summary>
/// <param name="ToolName">The tool the agent is invoking.</param>
/// <param name="Arguments">
/// The arguments the model supplied, exactly as the tool will receive them. Treat these as
/// untrusted: they are model output, and on an adversarial turn may be attacker-influenced.
/// </param>
/// <param name="AgentId">The agent making the call, or null outside a fully-scoped agent turn.</param>
/// <param name="ConversationId">The conversation the turn belongs to, or null.</param>
/// <param name="TurnNumber">The turn number within the conversation, or null.</param>
public sealed record ToolCallObservation(
    string ToolName,
    IReadOnlyDictionary<string, object?> Arguments,
    string? AgentId,
    string? ConversationId,
    int? TurnNumber);

/// <summary>What an observer decided about a tool call.</summary>
public enum ToolCallOutcome
{
    /// <summary>The observer has no objection; the call runs unless another observer objects.</summary>
    Proceed,

    /// <summary>The call is stopped. The model receives the observer's message in place of a result.</summary>
    Block,

    /// <summary>
    /// The call is suspended pending a human decision through the approval workflow. Approved, it
    /// runs; refused, timed out, or unroutable, it is blocked.
    /// </summary>
    RequireApproval
}

/// <summary>
/// An observer's ruling on one tool call.
/// </summary>
/// <param name="Outcome">What the observer decided.</param>
/// <param name="Reason">
/// Why, in operator-facing terms. Recorded in logs and audit and shown to a human approver. Never
/// relayed verbatim to the model — see <see cref="ToolCallVerdict.Block"/>.
/// </param>
public sealed record ToolCallVerdict(ToolCallOutcome Outcome, string? Reason = null)
{
    private static readonly ToolCallVerdict ProceedVerdict = new(ToolCallOutcome.Proceed);

    /// <summary>The observer has no objection to this call.</summary>
    public static ToolCallVerdict Proceed() => ProceedVerdict;

    /// <summary>
    /// Stops the call.
    /// </summary>
    /// <param name="reason">
    /// Operator-facing explanation, recorded in logs and audit. The model is told only that the
    /// tool is not permitted — the same generic message every other gate returns — so model-visible
    /// content never discloses which rule fired or how it is configured.
    /// </param>
    public static ToolCallVerdict Block(string reason) => new(ToolCallOutcome.Block, reason);

    /// <summary>
    /// Suspends the call pending a human decision.
    /// </summary>
    /// <param name="reason">Why a human must rule on this call. Shown to the approver.</param>
    /// <remarks>
    /// Requires the host to have configured tool approval routing. When it has not, there is nobody
    /// to ask, and the call is blocked rather than allowed through unjudged.
    /// </remarks>
    public static ToolCallVerdict RequireApproval(string reason) =>
        new(ToolCallOutcome.RequireApproval, reason);
}
