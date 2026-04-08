using Domain.Common.Config.AI;
using Domain.Common.Config.Azure;
using Domain.Common.Config.Cache;
using Domain.Common.Config.Connectors;
using Domain.Common.Config.Http;
using Domain.Common.Config.Infrastructure;
using Domain.Common.Config.Observability;

namespace Domain.Common.Config;

/// <summary>
/// Root configuration object for the agentic harness application.
/// Binds to the <c>AppConfig</c> section in appsettings.json and provides
/// strongly-typed access to all configuration subsections.
/// </summary>
/// <remarks>
/// This class is the single entry point for all application configuration.
/// Each subsection is represented by a nested configuration class, enabling
/// <c>IOptionsMonitor&lt;AppConfig&gt;</c> for runtime configuration changes.
/// <para>
/// Configuration hierarchy:
/// <code>
/// AppConfig
/// ├── Common         — General settings (thresholds, feature flags)
/// ├── Logging        — Logging infrastructure settings (paths, levels)
/// ├── Agent          — Agent execution settings (timeouts, token budgets)
/// ├── Http           — HTTP settings (CORS, authorization, maintenance)
/// ├── Infrastructure — State management, content providers
/// ├── Connectors     — External system connector integrations
/// ├── Observability  — Sampling, PII filtering, rate limiting, exporters
/// ├── AI             — MCP server/client, agent framework, model selection
/// ├── Azure          — Azure platform services (AppInsights, SQL, B2C, KeyVault)
/// └── Cache          — Caching strategy and Redis configuration
/// </code>
/// </para>
/// <para>
/// <strong>Mutable setters are required by <c>IOptionsMonitor&lt;T&gt;</c> binding.</strong>
/// Treat instances as read-only after DI setup. Do not mutate at runtime.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // In appsettings.json:
/// {
///   "AppConfig": {
///     "Common": { "SlowThresholdSec": 5 },
///     "Logging": { "LogsBasePath": "/var/logs/agent-harness" },
///     "Http": {
///       "CorsAllowedOrigins": "https://localhost:4200",
///       "Authorization": { "Enabled": false }
///     }
///   }
/// }
///
/// // In DI:
/// services.Configure&lt;AppConfig&gt;(configuration.GetSection("AppConfig"));
/// </code>
/// </example>
// Mutable setters required by IOptionsMonitor<T> binding. Treat as read-only after DI setup.
public class AppConfig
{
    /// <summary>
    /// Gets or sets the general application settings.
    /// </summary>
    public CommonConfig Common { get; set; } = new();

    /// <summary>
    /// Gets or sets the logging infrastructure settings.
    /// </summary>
    public LoggingConfig Logging { get; set; } = new();

    /// <summary>
    /// Gets or sets the agent execution settings.
    /// </summary>
    public AgentConfig Agent { get; set; } = new();

    /// <summary>
    /// Gets or sets the HTTP-related settings including CORS, authorization,
    /// and maintenance mode behavior.
    /// </summary>
    public HttpConfig Http { get; set; } = new();

    /// <summary>
    /// Gets or sets the Infrastructure services configuration including
    /// state management and content providers.
    /// </summary>
    public InfrastructureConfig Infrastructure { get; set; } = new();

    /// <summary>
    /// Gets or sets the external system connector integrations.
    /// Each connector wraps an external REST API (GitHub, Jira, Azure DevOps, Slack)
    /// behind a uniform <c>IConnectorClient</c> interface for agent tool invocation.
    /// </summary>
    public ConnectorsConfig Connectors { get; set; } = new();

    /// <summary>
    /// Gets or sets the observability pipeline configuration including
    /// tail-based sampling, PII filtering, rate limiting, and multi-backend export.
    /// </summary>
    public ObservabilityConfig Observability { get; set; } = new();

    /// <summary>
    /// Gets or sets the AI services configuration including MCP server/client
    /// settings, agent framework, and model selection.
    /// </summary>
    public AIConfig AI { get; set; } = new();

    /// <summary>
    /// Gets or sets the Azure platform services configuration including
    /// Application Insights, SQL, Blob Storage, AD B2C, and Key Vault.
    /// Services are only activated when their connection strings are configured.
    /// </summary>
    public AzureConfig Azure { get; set; } = new();

    /// <summary>
    /// Gets or sets the caching strategy and backing store configuration.
    /// </summary>
    public CacheConfig Cache { get; set; } = new();
}

/// <summary>
/// Configuration for agent execution including timeouts, content safety,
/// and token budget defaults.
/// </summary>
public class AgentConfig
{
    /// <summary>
    /// Gets or sets the default timeout in seconds for MediatR requests.
    /// Applied by <c>TimeoutBehavior</c> when a request does not specify its own timeout.
    /// </summary>
    /// <value>Default: 30 seconds.</value>
    public int DefaultRequestTimeoutSec { get; set; } = 30;

    /// <summary>
    /// Gets or sets the default token budget per agent session.
    /// Applied by <c>TokenBudgetBehavior</c> when no per-agent budget is configured.
    /// </summary>
    /// <value>Default: 128,000 tokens.</value>
    public long DefaultTokenBudget { get; set; } = 128_000;
}

/// <summary>
/// General application settings that apply across all layers.
/// </summary>
public class CommonConfig
{
    /// <summary>
    /// Gets or sets the application display name, used in OTel resource attributes
    /// and health check reporting.
    /// </summary>
    /// <value>Default: "AgenticHarness".</value>
    public string ApplicationName { get; set; } = "AgenticHarness";

    /// <summary>
    /// Gets or sets the application version, used in OTel resource attributes
    /// and API versioning metadata.
    /// </summary>
    /// <value>Default: "1.0".</value>
    public string ApplicationVersion { get; set; } = "1.0";

    /// <summary>
    /// Gets or sets the threshold in seconds beyond which a request is considered slow.
    /// Used by <c>RequestPerformanceBehavior</c> to log warnings for slow operations.
    /// </summary>
    /// <value>Default: 5 seconds.</value>
    public int SlowThresholdSec { get; set; } = 5;
}

/// <summary>
/// Configuration for the logging infrastructure including file paths,
/// named pipe settings, and structured output options.
/// </summary>
public class LoggingConfig
{
    /// <summary>
    /// Gets or sets the base directory for file-based log output.
    /// Run-specific subdirectories are created beneath this path.
    /// </summary>
    /// <value>
    /// Default: <c>null</c>. Must be explicitly configured in appsettings.json.
    /// When null, file-based logging providers will not write output.
    /// </value>
    public string? LogsBasePath { get; set; }

    /// <summary>
    /// Gets or sets the name of the named pipe for real-time log streaming.
    /// </summary>
    /// <value>Default: "agentic-harness-logs".</value>
    public string PipeName { get; set; } = "agentic-harness-logs";

    /// <summary>
    /// Gets or sets whether structured JSON logging (JSONL) is enabled.
    /// </summary>
    /// <value>Default: true.</value>
    public bool EnableStructuredJson { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum number of log entries retained by the
    /// in-memory ring buffer for diagnostics endpoints.
    /// </summary>
    /// <value>Default: 500.</value>
    public int RingBufferCapacity { get; set; } = 500;
}
