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

        violation = null;
        return true;
    }
}
