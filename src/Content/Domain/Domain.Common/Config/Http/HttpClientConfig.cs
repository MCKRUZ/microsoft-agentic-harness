using System.ComponentModel.DataAnnotations;

namespace Domain.Common.Config.Http;

/// <summary>
/// Base configuration for HTTP clients providing common settings such as
/// base address, timeout, health check, and service discovery options.
/// </summary>
/// <remarks>
/// Concrete HTTP client configurations should inherit from this class and
/// add service-specific settings. The base class provides sensible defaults
/// for all properties while allowing override via appsettings.json binding.
/// <para>
/// <strong>Mutable setters are required by <c>IOptionsMonitor&lt;T&gt;</c> binding.</strong>
/// Treat instances as read-only after DI setup.
/// </para>
/// </remarks>
public abstract class HttpClientConfig
{
    /// <summary>
    /// Gets or sets the list of alternative endpoint URLs for service discovery.
    /// When <see cref="EnableServiceDiscovery"/> is true, these endpoints are
    /// health-checked alongside <see cref="BaseAddress"/> to find the healthiest.
    /// </summary>
    public List<string> AlternativeEndpoints { get; set; } = [];

    /// <summary>
    /// Gets or sets the primary base address for the HTTP client.
    /// </summary>
    [Required]
    public string BaseAddress { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the duration for which resolved endpoints are cached.
    /// </summary>
    /// <value>Default: 5 minutes.</value>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets or sets whether service discovery is enabled for this client.
    /// When true, alternative endpoints are health-checked to select the best one.
    /// </summary>
    public bool EnableServiceDiscovery { get; set; }

    /// <summary>
    /// Gets or sets the hosting environment name (e.g., "Development", "Production").
    /// Used to determine environment-specific behaviors such as certificate validation.
    /// </summary>
    [Required]
    public string Environment { get; set; } = "Development";

    /// <summary>
    /// Gets or sets the path appended to endpoints during health checks.
    /// </summary>
    /// <value>Default: "/health".</value>
    public string HealthCheckPath { get; set; } = "/health";

    /// <summary>
    /// Gets or sets the timeout for individual health check requests.
    /// </summary>
    /// <value>Default: 10 seconds.</value>
    public TimeSpan HealthCheckTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets a value indicating whether the current environment is Development.
    /// </summary>
    public bool IsDevelopment =>
        string.Equals(Microsoft.Extensions.Hosting.Environments.Development, Environment, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets or sets the default request timeout for this HTTP client.
    /// </summary>
    /// <value>Default: 30 seconds.</value>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
}
