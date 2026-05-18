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
}
