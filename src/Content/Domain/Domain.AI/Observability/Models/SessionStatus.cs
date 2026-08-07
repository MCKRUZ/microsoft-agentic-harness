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
/// <strong>Cancellation is a state of its own again (#301).</strong> It could not be, for a while:
/// the schema had no word for it and the template had no way to change the shape of a database that
/// already held data, so widening the constraint would have reached CI and fresh installs while
/// leaving every real installation rejecting the new value exactly as before. A cancelled run was
/// recorded as <see cref="Error"/> instead, which overstated the failure rate on every dashboard.
/// A versioned migration runner now delivers schema changes to existing databases, so
/// <see cref="Cancelled"/> is carried by migration <c>005_sessions_status_cancelled.sql</c> and the
/// compromise is retired.
/// </para>
/// </remarks>
public enum SessionStatus
{
    /// <summary>The session is open and the conversation may still receive turns.</summary>
    Active,

    /// <summary>The session finished normally.</summary>
    Completed,

    /// <summary>The session stopped because of a failure.</summary>
    Error,

    /// <summary>
    /// The session was stopped on purpose — by the caller, by a disconnect, or by a cancellation
    /// token — before it could finish.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Error"/> because it is not a failure, and the distinction is visible:
    /// this value is what the sessions list and the Grafana <c>$status</c> filter show an operator,
    /// so while cancellations were written as <see cref="Error"/> every ordinary disconnect read as
    /// something having gone wrong.
    /// </remarks>
    /// <remarks>
    /// It does <em>not</em> move any error-rate number, and an earlier version of this comment said
    /// it did. No panel in <c>Dashboards/</c> computes a rate from <c>status = 'error'</c> — the
    /// error-rate tiles are Prometheus counters over tool errors. The claim was repeated in five
    /// places, which is how an unchecked justification becomes something everyone assumes was checked.
    /// </remarks>
    Cancelled,
}

/// <summary>Converts <see cref="SessionStatus"/> to the literal the observability schema stores.</summary>
public static class SessionStatusExtensions
{
    /// <summary>
    /// Returns the exact text the <c>sessions.status</c> CHECK constraint accepts for this state.
    /// </summary>
    /// <param name="status">The state to write.</param>
    /// <returns>One of <c>active</c>, <c>completed</c>, <c>error</c>, or <c>cancelled</c>.</returns>
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
        SessionStatus.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(
            nameof(status), status, "No schema literal is defined for this session status."),
    };
}
