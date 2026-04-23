namespace Domain.AI.Telemetry.Conventions;

/// <summary>Cost budget tracking telemetry attributes and metric names.</summary>
public static class BudgetConventions
{
    /// <summary>Current spend in USD for the active period. Tags: period.</summary>
    public const string CurrentSpend = "agent.budget.current_spend";
    /// <summary>Budget status as numeric value (0=clear, 1=warning, 2=critical). Tags: period.</summary>
    public const string Status = "agent.budget.status";
    /// <summary>Warning threshold in USD. Tags: period.</summary>
    public const string ThresholdWarning = "agent.budget.threshold_warning";
    /// <summary>Critical threshold in USD. Tags: period.</summary>
    public const string ThresholdCritical = "agent.budget.threshold_critical";
    /// <summary>Budget period dimension label.</summary>
    public const string Period = "agent.budget.period";

    public static class StatusValues
    {
        public const int Clear = 0;
        public const int Warning = 1;
        public const int Critical = 2;
    }

    public static class PeriodValues
    {
        public const string Daily = "daily";
        public const string Weekly = "weekly";
        public const string Monthly = "monthly";
    }
}
