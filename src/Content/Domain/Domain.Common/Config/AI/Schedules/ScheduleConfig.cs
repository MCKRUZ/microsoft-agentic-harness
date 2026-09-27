namespace Domain.Common.Config.AI.Schedules;

/// <summary>
/// Configuration for the recurring-schedule seam (#593) — cron-driven, timezone-aware definitions
/// that enqueue a run on a clock rather than in response to a caller. Bound from
/// <c>AppConfig:AI:Schedules</c>.
/// </summary>
/// <remarks>
/// Off by default, matching <see cref="Domain.Common.Config.AI.WorkflowSubmission.WorkflowSubmissionConfig"/>'s
/// convention: a consumer who binds this section without setting anything gets no recurring-work
/// surface at all.
/// </remarks>
public sealed class ScheduleConfig
{
    /// <summary>
    /// Master toggle. When disabled (the default), the tick service still runs (registered
    /// unconditionally alongside the rest of the run substrate) but does nothing every tick, and the
    /// schedule-management CQRS surface refuses every request.
    /// </summary>
    /// <value>Default: false</value>
    public bool Enabled { get; set; }

    /// <summary>How often the tick service checks for due schedules.</summary>
    /// <value>Default: 30 seconds</value>
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Maximum number of schedules one caller may have at once.</summary>
    /// <value>Default: 50</value>
    public int MaxSchedulesPerOwner { get; set; } = 50;

    /// <summary>Path to the SQLite database file backing the durable schedule store.</summary>
    /// <value>Default: "data/schedules.db"</value>
    public string DatabasePath { get; set; } = "data/schedules.db";
}
