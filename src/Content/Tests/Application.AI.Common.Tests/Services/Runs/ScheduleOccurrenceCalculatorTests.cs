using Application.AI.Common.Services.Runs;
using FluentAssertions;

namespace Application.AI.Common.Tests.Services.Runs;

/// <summary>Tests for <see cref="ScheduleOccurrenceCalculator"/>.</summary>
public sealed class ScheduleOccurrenceCalculatorTests
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ComputeNextOccurrence_EveryThirtyMinutes_ReturnsTheNextHalfHourBoundary()
    {
        var earliestAllowed = new DateTimeOffset(2026, 9, 22, 12, 5, 0, TimeSpan.Zero);

        var next = ScheduleOccurrenceCalculator.ComputeNextOccurrence(
            "*/30 * * * *", "UTC", earliestAllowed, activeHoursStart: null, activeHoursEnd: null);

        next.Should().Be(new DateTimeOffset(2026, 9, 22, 12, 30, 0, TimeSpan.Zero));
    }

    [Fact]
    public void ComputeNextOccurrence_ExactBoundary_IsInclusiveOfEarliestAllowed()
    {
        var next = ScheduleOccurrenceCalculator.ComputeNextOccurrence(
            "*/30 * * * *", "UTC", Noon, activeHoursStart: null, activeHoursEnd: null);

        next.Should().Be(Noon, "a schedule claimed exactly on its own boundary must not skip to the following one");
    }

    [Fact]
    public void ComputeNextOccurrence_CandidateInsideActiveHours_IsReturnedUnchanged()
    {
        // Every 30 minutes, restricted to 09:00-17:00. 16:30 is both a valid cron tick and inside
        // the window, so it should be returned exactly.
        var insideWindow = new DateTimeOffset(2026, 9, 22, 16, 30, 0, TimeSpan.Zero);

        var next = ScheduleOccurrenceCalculator.ComputeNextOccurrence(
            "*/30 * * * *", "UTC", insideWindow,
            activeHoursStart: TimeSpan.FromHours(9), activeHoursEnd: TimeSpan.FromHours(17));

        next.Should().Be(insideWindow);
    }

    [Fact]
    public void ComputeNextOccurrence_LastTickBeforeWindowCloses_AdvancesToNextDaysWindowOpen()
    {
        // 17:00 itself is the cron's next tick from 16:45, but the window end is exclusive, so 17:00
        // does not qualify and the search must skip all the way to tomorrow's opening.
        var justBeforeClose = new DateTimeOffset(2026, 9, 22, 16, 45, 0, TimeSpan.Zero);

        var next = ScheduleOccurrenceCalculator.ComputeNextOccurrence(
            "*/30 * * * *", "UTC", justBeforeClose,
            activeHoursStart: TimeSpan.FromHours(9), activeHoursEnd: TimeSpan.FromHours(17));

        next.Should().Be(new DateTimeOffset(2026, 9, 23, 9, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void ComputeNextOccurrence_EveryMinuteJustAfterWindowCloses_SkipsToNextDaysWindowOpen()
    {
        var justAfterClose = new DateTimeOffset(2026, 9, 22, 17, 0, 0, TimeSpan.Zero);

        var next = ScheduleOccurrenceCalculator.ComputeNextOccurrence(
            "* * * * *", "UTC", justAfterClose,
            activeHoursStart: TimeSpan.FromHours(9), activeHoursEnd: TimeSpan.FromHours(17));

        next.Should().Be(new DateTimeOffset(2026, 9, 23, 9, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void ComputeNextOccurrence_UnknownTimeZone_Throws()
    {
        var act = () => ScheduleOccurrenceCalculator.ComputeNextOccurrence(
            "*/30 * * * *", "Not/A_Real_Zone", Noon, null, null);

        act.Should().Throw<TimeZoneNotFoundException>();
    }

    [Fact]
    public void ComputeNextOccurrence_MalformedCron_Throws()
    {
        var act = () => ScheduleOccurrenceCalculator.ComputeNextOccurrence(
            "not a cron expression", "UTC", Noon, null, null);

        act.Should().Throw<FormatException>("Cronos.CronFormatException derives from FormatException");
    }

    [Fact]
    public void HasMissedMultipleOccurrences_OneIntervalLate_ReturnsFalse()
    {
        // Due five minutes ago on a 30-minute cadence: normal lateness, not a backlog miss — only one
        // occurrence (the due one itself) has come and gone.
        var lastFireAt = new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);
        var now = lastFireAt.AddMinutes(5);

        ScheduleOccurrenceCalculator.HasMissedMultipleOccurrences("*/30 * * * *", "UTC", lastFireAt, now)
            .Should().BeFalse();
    }

    [Fact]
    public void HasMissedMultipleOccurrences_SeveralIntervalsLate_ReturnsTrue()
    {
        // The host was down for two hours on a 30-minute cadence: several occurrences missed.
        var lastFireAt = new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);
        var now = lastFireAt.AddHours(2);

        ScheduleOccurrenceCalculator.HasMissedMultipleOccurrences("*/30 * * * *", "UTC", lastFireAt, now)
            .Should().BeTrue();
    }
}
