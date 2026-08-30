namespace Application.AI.Common.StructuredOutput;

/// <summary>
/// Why a call to <see cref="Interfaces.AI.IStructuredOutputInvoker"/>'s invoke method terminated.
/// </summary>
/// <remarks>
/// Modelled on <c>Evaluation.Outcomes.LlmJudgeOutcome</c> — the same distinct-failure-mode shape,
/// so a bad parse (<see cref="Malformed"/>) is never confused with a repair attempt that itself
/// failed (<see cref="RepairFailed"/>) or with a transport failure (<see cref="InvocationFailed"/>).
/// </remarks>
public enum StructuredOutcome
{
    /// <summary>The model returned JSON that parsed and deserialized into the target type.</summary>
    Parsed = 0,

    /// <summary>
    /// Both attempts (first + repair) returned JSON that either did not parse or failed to
    /// deserialize into the target type (including a missing <see langword="required"/> member).
    /// </summary>
    Malformed = 1,

    /// <summary>An infrastructure exception escaped the call (network, provider, etc.).</summary>
    InvocationFailed = 2,

    /// <summary>
    /// The first attempt was malformed and the repair round-trip itself failed — distinct from
    /// <see cref="Malformed"/> so a caller can tell "never got valid JSON" apart from "got it wrong
    /// once, then the repair attempt errored rather than merely being malformed again" (see
    /// <see cref="InvocationFailed"/> for the latter's actual value if it also carries an exception).
    /// </summary>
    RepairFailed = 3,

    /// <summary>The model returned an empty or whitespace-only body. No repair is attempted —
    /// a stricter format instruction cannot fix "the model said nothing."</summary>
    EmptyResponse = 4,
}
