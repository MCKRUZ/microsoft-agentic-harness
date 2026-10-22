namespace Domain.Common.Config.AI.Planner;

/// <summary>
/// System-level configuration for the planner subsystem.
/// Bound from <c>AppConfig:AI:Planner</c> in appsettings.json.
/// </summary>
public sealed class PlannerOptions
{
    /// <summary>Master toggle for the planner subsystem.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Maximum plans executing simultaneously across all sessions.</summary>
    public int MaxConcurrentPlans { get; set; } = 50;

    /// <summary>Maximum concurrent steps within a single plan execution (feeds <c>SemaphoreSlim</c>).</summary>
    public int MaxParallelSteps { get; set; } = 10;

    /// <summary>Default plan-level timeout in minutes.</summary>
    public int PlanTimeoutMinutes { get; set; } = 30;

    /// <summary>Maximum sub-plan nesting depth to prevent infinite recursion.</summary>
    public int MaxSubPlanDepth { get; set; } = 5;

    /// <summary>Apply EF Core migrations at startup. Set to false in production environments.</summary>
    public bool AutoMigrate { get; set; } = true;

    /// <summary>SQLite database file path relative to <c>AppContext.BaseDirectory</c>.</summary>
    public string DatabasePath { get; set; } = "data/planner.db";

    /// <summary>Persist plan state after every step transition for crash recovery.</summary>
    public bool CheckpointAfterEachStep { get; set; } = true;

    /// <summary>
    /// How long a plan is kept after anything last happened to it (#901). Defaults to 90 days.
    /// </summary>
    /// <remarks>
    /// A plan holds its steps' prompts, outputs and errors, which can repeat what a person asked or what a tool returned.
    /// "Anything happened" is the plan being saved, a step starting or finishing, or an execution-log entry. A plan nothing
    /// has happened to for this long is deleted whatever its state: finished, never run, or stopped waiting on an approval
    /// nobody gave. Lengthen it if your users save plans to run much later.
    /// </remarks>
    public RetentionPeriodConfig Retention { get; set; } = new();
}
