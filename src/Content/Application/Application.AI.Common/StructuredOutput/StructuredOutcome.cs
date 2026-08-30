namespace Application.AI.Common.StructuredOutput;

/// <summary>
/// Why a call to <see cref="Interfaces.AI.IStructuredOutputInvoker"/>'s invoke method terminated.
/// </summary>
/// <remarks>
/// Modelled on <c>Evaluation.Outcomes.LlmJudgeOutcome</c>'s distinct-failure-mode shape, adapted to
/// this invoker's actual control flow. There is deliberately no separate "malformed, no repair
/// attempted" value: the two-attempt loop unconditionally schedules a repair after any parse
/// failure on the first attempt, so by the time a call can fail on unparseable JSON, a repair
/// always already happened — <see cref="RepairFailed"/> is reached, never a bare "malformed."
/// Modelling a value that no code path can produce is the "outcomes collapsed by construction"
/// defect shape this codebase's own history tracks — cheaper to not add the value than to add a
/// test proving it's unreachable.
/// </remarks>
public enum StructuredOutcome
{
    /// <summary>The model returned JSON that parsed and deserialized into the target type.</summary>
    Parsed = 0,

    /// <summary>An infrastructure exception escaped the call (network, provider, etc.).</summary>
    InvocationFailed = 1,

    /// <summary>
    /// Both attempts (first + the one repair round-trip) returned JSON that either did not parse
    /// or failed to deserialize into the target type (including a missing
    /// <see langword="required"/> member).
    /// </summary>
    RepairFailed = 2,

    /// <summary>The model returned an empty or whitespace-only body. No repair is attempted —
    /// a stricter format instruction cannot fix "the model said nothing."</summary>
    EmptyResponse = 3,
}
