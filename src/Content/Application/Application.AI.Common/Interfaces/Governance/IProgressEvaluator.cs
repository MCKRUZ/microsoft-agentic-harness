namespace Application.AI.Common.Interfaces.Governance;

/// <summary>
/// Deterministic spin / no-progress detector for the agent's live tool-call path. Observes the
/// sequence of tool calls within a turn and decides whether the agent is making progress or looping.
/// </summary>
/// <remarks>
/// <para>
/// Complements <see cref="IToolInvocationGovernor"/> at the same invocation chokepoint
/// (<c>GovernedAIFunction</c>): the governor answers "may this tool run?" while the evaluator answers
/// "is the agent still making progress?". Detection is pure call-signature counting — no model
/// involvement — so it is cheap, deterministic, and unit-testable.
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
    /// Records a tool call and decides whether the agent is spinning.
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
    ProgressVerdict Evaluate(string toolName, Func<string?> argumentsSignatureFactory);

    /// <summary>
    /// The distinct escalation reason codes the guard raised this turn. Empty unless a spin was
    /// detected while configured for <c>ProgressGuardAction.Escalate</c>. The turn handler folds these
    /// into the turn's <c>GovernanceTrace.EscalationReasonCodes</c>.
    /// </summary>
    IReadOnlyList<string> EscalationReasonCodes { get; }

    /// <summary>Clears the recorded call history and escalations so the next turn starts clean.</summary>
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
