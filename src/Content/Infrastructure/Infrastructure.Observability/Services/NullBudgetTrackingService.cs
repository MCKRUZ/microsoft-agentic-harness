using Application.AI.Common.Interfaces;

namespace Infrastructure.Observability.Services;

/// <summary>
/// No-op budget tracking service used when budget tracking is disabled.
/// All operations are silent no-ops returning safe defaults.
/// </summary>
internal sealed class NullBudgetTrackingService : IBudgetTrackingService
{
    public void RecordSpend(double amountUsd, string agentName) { }
    public int GetCurrentStatus(string period) => 0;
    public double GetCurrentSpend(string period) => 0;
    public double GetThreshold(string period, string level) => 0;
}
