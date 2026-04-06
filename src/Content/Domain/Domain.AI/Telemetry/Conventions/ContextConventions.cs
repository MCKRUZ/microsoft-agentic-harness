namespace Domain.AI.Telemetry.Conventions;

/// <summary>Context budget telemetry attributes and metric names.</summary>
public static class ContextConventions
{
    public const string BudgetLimit = "agent.context.budget_limit";
    public const string BudgetUsed = "agent.context.budget_used";
    public const string CompactionReason = "agent.context.compaction_reason";
    public const string Compactions = "agent.context.compactions";
}
