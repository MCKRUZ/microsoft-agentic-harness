namespace Domain.Common.Config.Azure;

/// <summary>
/// Azure Application Insights configuration for telemetry and monitoring.
/// </summary>
public class ApplicationInsightsConfig
{
    /// <summary>
    /// Gets or sets the Application Insights connection string.
    /// When null, Application Insights export and health checks are disabled.
    /// </summary>
    public string? ConnectionString { get; set; }
}
