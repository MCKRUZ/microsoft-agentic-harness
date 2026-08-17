namespace Application.AI.Common.Evaluation.Outcomes;

/// <summary>
/// Why a call to <see cref="Interfaces.ILlmJudge.JudgeAsync"/> terminated.
/// </summary>
public enum LlmJudgeOutcome
{
    /// <summary>The judge returned valid JSON that deserialized into a score and reasoning.</summary>
    Parsed = 0,

    /// <summary>Both attempts (first + stricter retry) returned malformed JSON. Soft-fail to Warn.</summary>
    Malformed = 1,

    /// <summary>An infrastructure exception escaped the call (network, provider, etc.). Soft-fail to Warn.</summary>
    InvocationFailed = 2,

    /// <summary>
    /// The judge returned valid JSON on both attempts, but the response failed the strict
    /// verdict contract (see <c>JudgeVerdictContract</c>) — e.g. a failing score whose
    /// <c>violated_clause</c> did not appear verbatim in the rubric it was given. Distinct
    /// from <see cref="Malformed"/>: the JSON parsed fine, the *content* didn't hold up.
    /// Soft-fail to Warn, same as the other non-Parsed outcomes.
    /// </summary>
    ContractViolation = 3,
}
