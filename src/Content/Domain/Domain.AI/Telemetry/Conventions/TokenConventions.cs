namespace Domain.AI.Telemetry.Conventions;

/// <summary>Token usage telemetry attributes.</summary>
public static class TokenConventions
{
    public const string Input = "agent.tokens.input";
    public const string Output = "agent.tokens.output";
    public const string Total = "agent.tokens.total";
    public const string BudgetLimit = "agent.tokens.budget_limit";
    public const string BudgetUsed = "agent.tokens.budget_used";
    public const string BudgetPercent = "agent.tokens.budget_pct";
}
