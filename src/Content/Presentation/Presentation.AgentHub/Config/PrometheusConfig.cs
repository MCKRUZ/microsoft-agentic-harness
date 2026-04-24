namespace Presentation.AgentHub.Config;

/// <summary>
/// Configuration for the Prometheus query proxy.
/// Bound from <c>AppConfig:Prometheus</c> in appsettings.json.
/// </summary>
public sealed record PrometheusConfig
{
    /// <summary>Base URL of the Prometheus server (e.g. <c>http://localhost:9090</c>).</summary>
    public string BaseUrl { get; init; } = "http://localhost:9090";

    /// <summary>HTTP request timeout in seconds for Prometheus API calls.</summary>
    public int TimeoutSeconds { get; init; } = 30;
}
