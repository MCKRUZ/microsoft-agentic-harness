namespace Domain.Common.Helpers;

/// <summary>
/// A "warn once per process" gate: a static <see cref="int"/> flag flipped exactly once via
/// <see cref="Interlocked.Exchange(ref int, int)"/>, so a misconfiguration that recurs on every
/// call (an empty roster, a disabled subsystem) logs a single actionable line for the life of the
/// process instead of spamming one per call.
/// </summary>
/// <remarks>
/// Extracted after the identical <c>Interlocked.Exchange(ref s_field, 1) == 0</c> idiom was
/// independently hand-copied four times (<c>EscalationToolApprovalRouter</c>'s
/// <c>s_blankApproversWarned</c>/<c>s_escalationDisabledWarned</c>, and
/// <c>CapabilityMatchSupervisor.Escalation</c>'s <c>s_delegationApproversWarned</c>/
/// <c>s_delegationEscalatedApproversWarned</c>) across two layers. The flag field itself must
/// still be declared by the caller — it is the caller's own static state — but the
/// exchange-and-guard mechanics live here once.
/// </remarks>
public static class WarnOnceGate
{
    /// <summary>
    /// Invokes <paramref name="warn"/> if and only if this is the first call to observe
    /// <paramref name="flag"/> as unset (0), atomically flipping it to 1 first so concurrent
    /// callers can never both observe unwarned and both log.
    /// </summary>
    /// <param name="flag">
    /// The caller's static warn-state field, passed by reference. Must be a field the caller owns
    /// for the lifetime of the guard — a local or a field on a per-call instance defeats the
    /// once-per-process intent.
    /// </param>
    /// <param name="warn">The warning action to run at most once.</param>
    public static void WarnOnce(ref int flag, Action warn)
    {
        ArgumentNullException.ThrowIfNull(warn);

        if (Interlocked.Exchange(ref flag, 1) == 0)
            warn();
    }
}
