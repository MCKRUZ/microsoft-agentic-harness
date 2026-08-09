namespace Application.AI.Common.Interfaces.Governance;

/// <summary>
/// Deterministic spin / no-progress detector for the agent's live tool-call path. Observes the
/// sequence of tool calls within a turn and decides whether the agent is making progress or looping.
/// </summary>
/// <remarks>
/// <para>
/// Complements <see cref="IToolInvocationGovernor"/> at the same invocation chokepoint: the governor
/// answers "may this tool run?" while the evaluator answers "is the agent still making progress?".
/// Detection is pure call-signature counting — no model involvement — so it is cheap, deterministic,
/// and unit-testable.
/// </para>
/// <para>
/// <strong>Deciding and recording are ONE operation, and separating them is a defect.</strong> This
/// looks like poor separation of concerns and has been proposed as a cleanup; it is neither. The
/// harness invokes an assistant message's tool calls <em>in parallel</em>
/// (<c>AllowConcurrentInvocation</c> is set by the agent factory), against the one turn-scoped
/// evaluator. Answering and recording inside a single critical section is what makes a batch of
/// identical calls serialise, so the second one sees the first. Split into "ask now, record later" —
/// even with nothing at all between the two calls — every member of the batch asks before any of them
/// has been recorded, they are all told they are the first, and <strong>the entire batch is
/// admitted</strong>. <c>ProgressEvaluatorConcurrencyTests</c> carries the measurement and is the
/// control that fails if anyone tries it again.
/// </para>
/// <para>
/// The property that separating them was meant to buy — that a call refused by some other gate is
/// never counted — is instead a property of <em>where</em> the chain calls this: at the single point
/// where a call has cleared every stage, below every path that returns a refusal. See
/// <see cref="IToolCallAdmissionPipeline"/>.
/// </para>
/// <para>
/// Scoped to one agent turn. Nested MediatR sends within a conversation share one DI scope (and thus
/// one evaluator instance), so a multi-turn conversation must <see cref="Reset"/> between turns —
/// mirroring the per-turn reset of the adjacent <see cref="IToolInvocationGovernor"/> and scoped
/// <c>ILlmUsageCapture</c>. The guard is opt-in via <c>GovernanceConfig.ProgressGuard.Enabled</c>;
/// when off, <see cref="Evaluate"/> always returns <see cref="ProgressVerdict.Continue"/>.
/// </para>
/// </remarks>
public interface IProgressEvaluator
{
    /// <summary>
    /// Records a tool call and decides whether the agent is spinning, as one atomic operation.
    /// </summary>
    /// <param name="toolName">The tool the agent is invoking.</param>
    /// <param name="argumentsSignatureFactory">
    /// Produces a stable, deterministic signature of the call arguments. Two calls with the same tool
    /// and the same signature are treated as identical; the factory may return null/empty for a
    /// no-argument call. <strong>Invoked only when the guard is enabled</strong>, so callers can pass a
    /// closure that serialises arguments without paying that cost on the disabled (default) path.
    /// </param>
    /// <returns>
    /// <see cref="ProgressVerdict.Continue"/> to allow the call, or a halt verdict carrying a
    /// model-facing message to return in place of the tool result. When the guard is disabled the
    /// evaluator records nothing, never invokes the factory, and always returns
    /// <see cref="ProgressVerdict.Continue"/>.
    /// </returns>
    /// <remarks>
    /// Call this only for a call that is going to run. It counts what it is given, so a caller that
    /// consults it before the other gates have finished would have it count calls that are then
    /// blocked — which is not a bookkeeping nicety but defeats the guard outright. An agent retrying a
    /// blocked call with one argument changed each time presents a fresh signature every attempt,
    /// resetting the no-progress counter every attempt, and never trips the guard it is spinning
    /// against. That shipped once.
    /// </remarks>
    ProgressVerdict Evaluate(string toolName, Func<string?> argumentsSignatureFactory);

    /// <summary>Clears the recorded call history so the next turn starts clean.</summary>
    /// <remarks>
    /// Escalation reason codes are <em>not</em> cleared here — they live on
    /// <see cref="IGovernanceTraceRecorder"/> with the rest of the turn's governance trail, and are
    /// cleared when that is reset.
    /// </remarks>
    void Reset();
}

/// <summary>
/// The result of evaluating a single tool call for progress.
/// </summary>
/// <param name="ShouldHalt">Whether the call should be broken instead of executed.</param>
/// <param name="HaltMessage">
/// When halting, the model-facing message returned in place of the tool result (the same string-result
/// shape the tool converter uses for errors). Null when the call should proceed.
/// </param>
public sealed record ProgressVerdict(bool ShouldHalt, string? HaltMessage = null)
{
    // The continue verdict is immutable and consumed by value, so a single shared instance avoids a
    // per-tool-call allocation on the hot path (every permitted call and every disabled-guard call).
    private static readonly ProgressVerdict ContinueVerdict = new(false);

    /// <summary>A verdict allowing the call to proceed.</summary>
    public static ProgressVerdict Continue() => ContinueVerdict;

    /// <summary>A verdict breaking the loop, carrying the model-facing halt message.</summary>
    public static ProgressVerdict Halt(string haltMessage) => new(true, haltMessage);
}
