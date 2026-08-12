using Domain.AI.Escalation;

namespace Application.AI.Common.Interfaces.Escalation;

/// <summary>
/// The creation-time invariants every escalation must satisfy before it may become live and
/// decidable. Extracted so the durable rehydration path enforces exactly the same rules the
/// in-process creation path does.
/// </summary>
/// <remarks>
/// <para>
/// Rehydration reads from a SQLite file — untrusted input, not a value this process just
/// constructed. Without these checks a hand-edited or corrupted row could produce a live
/// escalation that violates an invariant the creation path deliberately fail-closes on:
/// </para>
/// <list type="bullet">
///   <item><description>
///     An <b>empty approver roster</b> can never be legitimately approved, yet the AllOf
///     strategy treats "nobody pending" as vacuously unanimous and a timeout action of
///     <c>Approve</c> would then grant it silently.
///   </description></item>
///   <item><description>
///     A <b>Quorum threshold of zero</b> (or one exceeding the roster size) is the same
///     failure in a different shape: either instantly satisfied by no votes, or impossible.
///   </description></item>
///   <item><description>
///     A <b>timeout beyond <see cref="MaxTimeoutSeconds"/></b> overflows
///     <see cref="TimeSpan"/>-based delays; the resulting throw is swallowed by the timeout
///     loop's catch-all and the escalation becomes immortal — pending forever with no
///     expiry.
///   </description></item>
///   <item><description>
///     <see cref="EscalationTimeoutAction.Approve"/> paired with
///     <see cref="EscalationPriority.Critical"/> would auto-approve the highest-risk class of
///     action purely because nobody was watching when the clock ran out — a safety gate that
///     fails open under load, which is the one failure mode an approval gate exists to
///     prevent. <see cref="EscalationTimeoutAction.Approve"/>'s own documentation states this
///     pairing "should never" occur; this is that rule enforced, at the runtime chokepoint
///     every request passes through on both creation and rehydration, rather than left as a
///     comment.
///   </description></item>
///   <item><description>
///     An <b>undefined enum value</b> for <see cref="EscalationRequest.ApprovalStrategy"/>,
///     <see cref="EscalationRequest.TimeoutAction"/>, or <see cref="EscalationRequest.Priority"/>
///     — reachable only via a hand-edited or corrupted durable row, since every in-process
///     constructor path is closed by config validation — would otherwise surface far downstream:
///     an undefined strategy throws inside <c>GetRequiredKeyedService</c> mid-resolution rather
///     than being refused up front.
///   </description></item>
/// </list>
/// </remarks>
public static class EscalationRequestInvariants
{
    /// <summary>
    /// Upper bound on <see cref="EscalationRequest.TimeoutSeconds"/>, in seconds (~24 days).
    /// Comfortably below the ~49.7-day ceiling at which a delay overflows, with room for the
    /// deadline arithmetic that resumes a rehydrated escalation's original budget.
    /// </summary>
    public const int MaxTimeoutSeconds = 24 * 24 * 60 * 60;

    /// <summary>
    /// Hard ceiling on <see cref="EscalationRequest.PriorFailureReason"/>, in characters. A second,
    /// stricter layer against a hand-edited durable row — the soft producer-side truncation lives
    /// in <c>EscalationConfig.RetryAttribution.MaxPriorFailureLength</c>, which
    /// <c>EscalationConfigValidator</c> ties to this value so the two can never be configured into
    /// disagreement.
    /// </summary>
    public const int MaxPriorFailureReasonLength = 4096;

    /// <summary>
    /// Validates an escalation request against every creation-time invariant.
    /// </summary>
    /// <param name="request">The request to validate.</param>
    /// <param name="violation">
    /// On failure, a short operator-readable description of the first violated invariant;
    /// null on success.
    /// </param>
    /// <returns>True when the request may become a live escalation.</returns>
    public static bool TryValidate(EscalationRequest request, out string? violation)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Checked first, ahead of every derived check below: an undefined enum value is a more
        // fundamental corruption than anything downstream logic can meaningfully say about it,
        // and a row can fail more than one check at once (e.g. an empty roster on a request
        // whose ApprovalStrategy is also undefined) — reporting the enum corruption first gives
        // the operator the more useful of the two causes rather than an incidental one.
        if (TryFindUndefinedEnum(request, out violation))
            return false;

        if (request.Approvers.Count == 0)
        {
            violation = "the approver roster is empty, so the escalation could never be approved";
            return false;
        }

        if (request.Approvers.Any(string.IsNullOrWhiteSpace))
        {
            violation = "the approver roster contains a blank identity";
            return false;
        }

        if (request.ApprovalStrategy == ApprovalStrategyType.Quorum &&
            (request.QuorumThreshold <= 0 || request.QuorumThreshold > request.Approvers.Count))
        {
            violation =
                $"the Quorum threshold ({request.QuorumThreshold}) is outside 1..{request.Approvers.Count}";
            return false;
        }

        if (request.TimeoutSeconds < 0)
        {
            violation = $"the timeout ({request.TimeoutSeconds}s) is negative";
            return false;
        }

        if (request.TimeoutSeconds > MaxTimeoutSeconds)
        {
            violation =
                $"the timeout ({request.TimeoutSeconds}s) exceeds the maximum of {MaxTimeoutSeconds}s";
            return false;
        }

        if (request.Priority == EscalationPriority.Critical &&
            request.TimeoutAction == EscalationTimeoutAction.Approve)
        {
            violation =
                "the timeout action is Approve at Critical priority — a Critical escalation must " +
                "never auto-approve on timeout";
            return false;
        }

        if (request.AttemptNumber < 1)
        {
            violation = $"the attempt number ({request.AttemptNumber}) is less than 1";
            return false;
        }

        // Deliberately NOT the mirror check (AttemptNumber > 1 && PriorFailureReason is null) —
        // that shape is what a legitimate LRU eviction of the failure memory produces, and
        // rejecting it would fail-close a valid escalation for a benign memory eviction.
        if (request.AttemptNumber == 1 && request.PriorFailureReason is not null)
        {
            violation = "attempt 1 carries a prior failure reason, which never happened";
            return false;
        }

        if (request.PriorFailureReason is { Length: > MaxPriorFailureReasonLength })
        {
            violation =
                $"the prior failure reason ({request.PriorFailureReason.Length} chars) exceeds the " +
                $"maximum of {MaxPriorFailureReasonLength}";
            return false;
        }

        violation = null;
        return true;
    }

    /// <summary>
    /// Checks <see cref="EscalationRequest.ApprovalStrategy"/>, <see cref="EscalationRequest.TimeoutAction"/>,
    /// and <see cref="EscalationRequest.Priority"/> against their defined members, in that order.
    /// </summary>
    private static bool TryFindUndefinedEnum(EscalationRequest request, out string? violation)
    {
        if (!Enum.IsDefined(request.ApprovalStrategy))
        {
            violation = $"the approval strategy ({(int)request.ApprovalStrategy}) is not a defined value";
            return true;
        }

        if (!Enum.IsDefined(request.TimeoutAction))
        {
            violation = $"the timeout action ({(int)request.TimeoutAction}) is not a defined value";
            return true;
        }

        if (!Enum.IsDefined(request.Priority))
        {
            violation = $"the priority ({(int)request.Priority}) is not a defined value";
            return true;
        }

        violation = null;
        return false;
    }
}
