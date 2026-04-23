namespace Application.AI.Common.Interfaces;

/// <summary>
/// Tracks cumulative LLM cost spend against configured budget thresholds
/// with a state machine (Clear/Warning/Critical) and period rollovers.
/// </summary>
public interface IBudgetTrackingService
{
    /// <summary>Records a spend amount in USD from an LLM call.</summary>
    /// <param name="amountUsd">Cost in USD.</param>
    /// <param name="agentName">The agent that incurred the cost.</param>
    void RecordSpend(double amountUsd, string agentName);

    /// <summary>Gets the current budget status for a period.</summary>
    /// <param name="period">The budget period (daily, weekly, monthly).</param>
    /// <returns>Status code: 0=clear, 1=warning, 2=critical.</returns>
    int GetCurrentStatus(string period);

    /// <summary>Gets the current cumulative spend in USD for a period.</summary>
    /// <param name="period">The budget period (daily, weekly, monthly).</param>
    double GetCurrentSpend(string period);

    /// <summary>Gets the configured threshold in USD for a period and alert level.</summary>
    /// <param name="period">The budget period (daily, weekly, monthly).</param>
    /// <param name="level">The alert level ("warning" or "critical").</param>
    double GetThreshold(string period, string level);
}
