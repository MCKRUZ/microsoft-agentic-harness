namespace Application.AI.Common.Interfaces.Changes;

/// <summary>
/// The severity of a single <see cref="PolicyFinding"/> reported by an
/// <see cref="IChangeProposalPolicy"/>. The <c>PolicyGate</c> maps findings to a
/// <c>GateResult</c> by comparing the highest finding severity to a configured
/// minimum-to-fail threshold.
/// </summary>
/// <remarks>
/// The ordering is meaningful: every member's integer value is a strict upper bound
/// of the previous member, so range checks like <c>severity &gt;= PolicyFindingSeverity.High</c>
/// are valid.
/// </remarks>
public enum PolicyFindingSeverity
{
    /// <summary>Informational finding. Default threshold treats Info as non-blocking.</summary>
    Info = 0,

    /// <summary>Low-priority concern, typically a stylistic or non-prod issue.</summary>
    Low = 1,

    /// <summary>Notable concern that warrants reviewer attention but does not block by default.</summary>
    Medium = 2,

    /// <summary>Material concern. Default threshold treats High as blocking.</summary>
    High = 3,

    /// <summary>
    /// Severe finding. Blocking regardless of threshold configuration — UNLESS the finding also sets
    /// <see cref="PolicyFinding.RequiresVerification"/>, in which case a model assigned this severity
    /// to an unconfirmed finding and <c>PolicyGate</c> deliberately does not trust it to block on its
    /// own (see that field's remarks). A policy that has independently confirmed a Critical finding
    /// sets <see cref="PolicyFinding.Blocking"/> instead, which always wins.
    /// </summary>
    Critical = 4
}
