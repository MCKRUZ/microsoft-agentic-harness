using Cronos;

namespace Application.AI.Common.Services.Runs;

/// <summary>
/// Computes when a schedule next fires, given its cron expression, time zone, cooldown floor, and
/// active-hours window. Shared by <c>CreateScheduleCommandHandler</c> (the initial
/// <c>ScheduleRecord.NextFireAt</c>) and the tick service (every subsequent occurrence), so the two
/// can never compute a schedule's cadence differently.
/// </summary>
/// <remarks>
/// Pure and stateless — no I/O, no ambient state — which is why it lives in the Application layer
/// rather than Infrastructure: both a CQRS handler and an Infrastructure background service need it,
/// and Application is the layer both already depend on.
/// </remarks>
public static class ScheduleOccurrenceCalculator
{
    /// <summary>
    /// Floor on how many candidate occurrences <see cref="ComputeNextOccurrence"/> will examine while
    /// searching for one inside an active-hours window, before giving up. Defensive only: each rejected
    /// candidate jumps straight to the window's next opening (see the loop below) rather than stepping
    /// minute-by-minute, so a genuinely satisfiable window/expression pair converges in a handful of
    /// candidates regardless of cron granularity — hitting this cap means the window and expression
    /// cannot both be satisfied at all, and the caller should surface an error rather than loop.
    /// </summary>
    public const int MaxActiveHoursSearchAttempts = 1000;

    /// <summary>
    /// Computes the next time <paramref name="cronExpression"/> fires at or after
    /// <paramref name="earliestAllowed"/>, honouring an optional active-hours window.
    /// </summary>
    /// <param name="cronExpression">Standard five-field cron expression.</param>
    /// <param name="timeZoneId">IANA time zone the cron expression's fields are evaluated in.</param>
    /// <param name="earliestAllowed">
    /// The earliest acceptable occurrence — the caller's cooldown floor already folded in.
    /// </param>
    /// <param name="activeHoursStart">Earliest time of day, in <paramref name="timeZoneId"/>, an occurrence may fall at. Null means no lower bound.</param>
    /// <param name="activeHoursEnd">Latest time of day (exclusive), in <paramref name="timeZoneId"/>, an occurrence may fall at. Null means no upper bound.</param>
    /// <exception cref="CronFormatException"><paramref name="cronExpression"/> is not a valid cron expression.</exception>
    /// <exception cref="TimeZoneNotFoundException"><paramref name="timeZoneId"/> does not resolve.</exception>
    /// <exception cref="InvalidOperationException">
    /// The cron expression has no future occurrence, or no occurrence within
    /// <see cref="MaxActiveHoursSearchAttempts"/> candidates falls inside the active-hours window.
    /// </exception>
    public static DateTimeOffset ComputeNextOccurrence(
        string cronExpression,
        string timeZoneId,
        DateTimeOffset earliestAllowed,
        TimeSpan? activeHoursStart,
        TimeSpan? activeHoursEnd)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cronExpression);
        ArgumentException.ThrowIfNullOrWhiteSpace(timeZoneId);

        var cron = CronExpression.Parse(cronExpression);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);

        var candidateUtc = earliestAllowed.UtcDateTime;
        for (var attempt = 0; attempt < MaxActiveHoursSearchAttempts; attempt++)
        {
            var next = cron.GetNextOccurrence(candidateUtc, zone, inclusive: true)
                ?? throw new InvalidOperationException(
                    $"Cron expression '{cronExpression}' has no future occurrence.");

            var nextOffset = new DateTimeOffset(DateTime.SpecifyKind(next, DateTimeKind.Utc));

            if (IsWithinActiveHours(nextOffset, zone, activeHoursStart, activeHoursEnd))
                return nextOffset;

            // Not in the window: jump straight to the window's next opening rather than stepping one
            // minute past this candidate. A minute-by-minute step needed up to ~1440 iterations for an
            // every-minute cron against a narrow window — enough to exceed MaxActiveHoursSearchAttempts
            // and wrongly reject a schedule that IS satisfiable. Jumping converges in a handful of
            // candidates regardless of cron granularity, since Cronos still finds the actual valid tick
            // at or after the opening on the next loop iteration.
            candidateUtc = NextWindowOpeningUtc(nextOffset, zone, activeHoursStart, activeHoursEnd).UtcDateTime;
        }

        throw new InvalidOperationException(
            $"Could not find an occurrence of '{cronExpression}' within the configured active-hours "
            + $"window after {MaxActiveHoursSearchAttempts} attempts.");
    }

    /// <summary>
    /// Whether <paramref name="instant"/> falls inside the active-hours window, in
    /// <paramref name="timeZoneId"/>. Exposed for callers (the tick service's catch-up-run path) that
    /// must confirm a specific moment — not just a computed occurrence — respects the window before
    /// acting on it.
    /// </summary>
    /// <param name="instant">The instant to check.</param>
    /// <param name="timeZoneId">IANA time zone the window's fields are evaluated in.</param>
    /// <param name="start">Earliest time of day an instant may fall at. Null means no lower bound.</param>
    /// <param name="end">Latest time of day (exclusive) an instant may fall at. Null means no upper bound.</param>
    /// <exception cref="TimeZoneNotFoundException"><paramref name="timeZoneId"/> does not resolve.</exception>
    public static bool IsWithinActiveHours(DateTimeOffset instant, string timeZoneId, TimeSpan? start, TimeSpan? end)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(timeZoneId);
        return IsWithinActiveHours(instant, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId), start, end);
    }

    private static bool IsWithinActiveHours(
        DateTimeOffset candidateUtc, TimeZoneInfo zone, TimeSpan? start, TimeSpan? end)
    {
        if (start is null && end is null)
            return true;

        var local = TimeZoneInfo.ConvertTime(candidateUtc, zone).TimeOfDay;
        return local >= (start ?? TimeSpan.Zero) && local < (end ?? TimeSpan.FromHours(24));
    }

    /// <summary>
    /// The next instant, at or after <paramref name="candidateUtc"/>, the active-hours window opens.
    /// Only called once <paramref name="candidateUtc"/> is already known to fall outside the window, so
    /// the window either hasn't opened yet today (jump to today's opening) or has already closed for
    /// the day (jump to tomorrow's opening) — <c>CreateScheduleCommandValidator</c> requires
    /// <c>ActiveHoursStart &lt; ActiveHoursEnd</c>, so a window never wraps past midnight and this
    /// two-way split is exhaustive.
    /// </summary>
    private static DateTimeOffset NextWindowOpeningUtc(
        DateTimeOffset candidateUtc, TimeZoneInfo zone, TimeSpan? start, TimeSpan? end)
    {
        var windowStart = start ?? TimeSpan.Zero;
        var local = TimeZoneInfo.ConvertTime(candidateUtc, zone);

        var openingDate = local.TimeOfDay < windowStart ? local.Date : local.Date.AddDays(1);
        var openingLocal = DateTime.SpecifyKind(openingDate + windowStart, DateTimeKind.Unspecified);

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(openingLocal, zone), TimeSpan.Zero);
    }

    /// <summary>
    /// Whether more than one occurrence of <paramref name="cronExpression"/> has come and gone since
    /// <paramref name="lastFireAt"/> — a genuine miss (the host was down for more than one interval),
    /// not just a caller observing a schedule that is normally due right now.
    /// </summary>
    /// <param name="cronExpression">Standard five-field cron expression.</param>
    /// <param name="timeZoneId">IANA time zone the cron expression's fields are evaluated in.</param>
    /// <param name="lastFireAt">The schedule's currently-recorded next/last fire time.</param>
    /// <param name="now">The current time.</param>
    public static bool HasMissedMultipleOccurrences(
        string cronExpression, string timeZoneId, DateTimeOffset lastFireAt, DateTimeOffset now)
    {
        var cron = CronExpression.Parse(cronExpression);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);

        var secondOccurrence = cron.GetNextOccurrence(lastFireAt.UtcDateTime, zone, inclusive: false);
        if (secondOccurrence is null)
            return false;

        return new DateTimeOffset(DateTime.SpecifyKind(secondOccurrence.Value, DateTimeKind.Utc)) <= now;
    }
}
