using Domain.AI.Telemetry.Conventions;

namespace Application.AI.Common.OpenTelemetry.Metrics;

/// <summary>
/// Agent configuration info metric name constant. Registered as an ObservableGauge
/// (always returns 1) with labels carrying configuration data per agent.
/// The actual gauge is created in <c>AgentConfigInfoService</c> which has access
/// to the agent configuration registry.
/// </summary>
public static class AgentConfigMetrics
{
    /// <summary>Config info metric name (ObservableGauge, always 1, labels carry config data).</summary>
    public static string ConfigInfoName => AgentConventions.ConfigInfo;
}
