namespace Domain.AI.Observability.Models;

/// <summary>
/// The terminal states a session row may be left in, and the only values the observability schema
/// accepts for its <c>status</c> column.
/// </summary>
/// <remarks>
/// <para>
/// This exists as a type rather than a convention because the convention did not hold. The column is
/// guarded by <c>CHECK (status IN ('active','completed','error'))</c>, and two of the four paths that
/// end a session were passing words outside it — the hub sent <c>"errored"</c> when a connection
/// dropped with an exception, and a self-contained run sent <c>"cancelled"</c>. Postgres rejected both
/// updates, and because a telemetry write must never fail the turn that produced it, the rejection was
/// caught and logged rather than raised. The visible effect was a session that never ended: no
/// <c>ended_at</c>, status still <c>active</c>, and a duration that grew for as long as the row lived.
/// </para>
/// <para>
/// Passing this type instead of a string moves that failure from a warning in a log nobody reads to an
/// error the compiler raises. <see cref="SessionStatusExtensions.ToDbValue"/> is the single place a
/// value becomes text, so the set the database accepts and the set the code can express cannot drift
/// apart again.
/// </para>
/// <para>
/// <strong>Cancellation is deliberately not a member.</strong> The schema has no word for it, and the
/// template has no way to migrate an existing database — <c>Dashboards/init-db/*.sql</c> is applied by
/// Postgres only when the data volume is first created, so widening the constraint would reach CI and
/// fresh installs while leaving every database that already holds data rejecting the new value exactly
/// as before. A cancelled run is therefore recorded as <see cref="Error"/> with its reason string
/// naming the cancellation, which overstates failures on the dashboard and is the known cost of the
/// choice. Giving cancellation its own state is tracked with the missing migration runner in #301.
/// </para>
/// </remarks>
public enum SessionStatus
{
    /// <summary>The session is open and the conversation may still receive turns.</summary>
    Active,

    /// <summary>The session finished normally.</summary>
    Completed,

    /// <summary>The session stopped because of a failure, or because it was cancelled.</summary>
    Error,
}

/// <summary>Converts <see cref="SessionStatus"/> to the literal the observability schema stores.</summary>
public static class SessionStatusExtensions
{
    /// <summary>
    /// Returns the exact text the <c>sessions.status</c> CHECK constraint accepts for this state.
    /// </summary>
    /// <param name="status">The state to write.</param>
    /// <returns>One of <c>active</c>, <c>completed</c>, or <c>error</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when a member is added to <see cref="SessionStatus"/> without a literal here. Failing
    /// loudly is the point: a silent fallback would reintroduce the swallowed write this type exists to
    /// prevent, and the throw surfaces at the first write rather than in a log.
    /// </exception>
    public static string ToDbValue(this SessionStatus status) => status switch
    {
        SessionStatus.Active => "active",
        SessionStatus.Completed => "completed",
        SessionStatus.Error => "error",
        _ => throw new ArgumentOutOfRangeException(
            nameof(status), status, "No schema literal is defined for this session status."),
    };
}
