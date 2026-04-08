using Azure.Identity;
using Domain.Common.Config;
using Domain.Common.Config.Cache;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.AzureAppConfiguration;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;

namespace Presentation.Common.Helpers;

/// <summary>
/// Centralized configuration loading utility that builds an <see cref="IConfiguration"/>
/// from multiple sources with environment-aware behavior and Azure integration.
/// </summary>
/// <remarks>
/// <para>
/// Configuration sources are loaded in ascending priority (later sources override earlier):
/// <list type="numbered">
///   <item><c>appsettings.json</c> — base configuration (required)</item>
///   <item><c>appsettings.{Environment}.json</c> — environment-specific overrides (optional)</item>
///   <item>User Secrets — local development secrets via <c>dotnet user-secrets</c></item>
///   <item>Environment variables — container and deployment settings</item>
///   <item>Azure Key Vault — secure secret storage (non-DEBUG builds only)</item>
///   <item>Azure App Configuration — centralized config management (non-DEBUG builds only)</item>
/// </list>
/// </para>
/// <para>
/// Azure sources (Key Vault and App Configuration) are conditionally loaded based on:
/// <list type="bullet">
///   <item>Compilation mode — excluded in <c>DEBUG</c> builds to avoid Azure dependencies during local development</item>
///   <item>Connection string presence — skipped when the corresponding connection string is not configured</item>
/// </list>
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // In Program.cs:
/// var config = AppConfigHelper.LoadAppConfig(Assembly.GetExecutingAssembly());
/// builder.Services.Configure&lt;AppConfig&gt;(config.GetSection("AppConfig"));
///
/// // Quick access to typed config:
/// var appConfig = AppConfigHelper.GetAppConfig(builder.Services);
/// </code>
/// </example>
/// <seealso href="https://docs.microsoft.com/en-us/aspnet/core/fundamentals/configuration/">
/// ASP.NET Core Configuration Documentation
/// </seealso>
public static class AppConfigHelper
{
    /// <summary>
    /// Loads application configuration from all configured sources in priority order.
    /// </summary>
    /// <param name="appAssembly">
    /// Optional assembly used for User Secrets discovery. Falls back to
    /// <see cref="Assembly.GetEntryAssembly"/> when <c>null</c>.
    /// </param>
    /// <returns>A fully built <see cref="IConfiguration"/> containing merged settings from all sources.</returns>
    /// <remarks>
    /// An initial configuration is bootstrapped from User Secrets first so that Azure
    /// connection strings stored in <c>secrets.json</c> are available for Key Vault
    /// and App Configuration providers.
    /// </remarks>
    public static IConfiguration LoadAppConfig(Assembly? appAssembly = null)
    {
        var debug = false;
#if DEBUG
        debug = true;
#endif

        appAssembly ??= Assembly.GetEntryAssembly();

        // Bootstrap an initial config so Azure connection strings from User Secrets
        // are available before the full configuration is built.
        var initialConfigBuilder = new ConfigurationBuilder();
        initialConfigBuilder.AddUserSecrets(appAssembly!, optional: true);
        var initialConfig = initialConfigBuilder.Build();

        var builder = new ConfigurationBuilder();

        builder.SetBasePath(Directory.GetCurrentDirectory());

        builder.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
        builder.AddJsonFile($"appsettings.{GetEnvironmentName()}.json", optional: true, reloadOnChange: true);

        builder.AddUserSecrets(appAssembly!, optional: true);
        builder.AddEnvironmentVariables();

        if (!debug && initialConfig["AzureKeyVaultUri"] != null)
            AddAzureKeyVault(builder, initialConfig);

        // Rebuild so Key Vault secrets are available for App Configuration connection string.
        initialConfig = builder.Build();

        if (!debug && !string.IsNullOrEmpty(initialConfig["AzureAppConfigConnectionString"]))
            AddAzureAppConfig(builder, initialConfig);

        return builder.Build();
    }

    /// <summary>
    /// Loads configuration and returns the strongly-typed <see cref="AppConfig"/> bound
    /// to the <c>AppConfig</c> section.
    /// </summary>
    /// <param name="services">
    /// The service collection (reserved for future DI-based configuration scenarios).
    /// </param>
    /// <returns>
    /// The bound <see cref="AppConfig"/> instance, or <c>null</c> if the section is missing.
    /// </returns>
    public static AppConfig? GetAppConfig(IServiceCollection services)
    {
        var configuration = LoadAppConfig();
        return configuration.GetSection("AppConfig").Get<AppConfig>();
    }

    /// <summary>
    /// Returns the current ASP.NET Core environment name from the
    /// <c>ASPNETCORE_ENVIRONMENT</c> environment variable.
    /// </summary>
    /// <returns>
    /// The environment name, or <c>"Development"</c> when the variable is not set.
    /// </returns>
    public static string GetEnvironmentName()
    {
        var environmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        return string.IsNullOrEmpty(environmentName) ? "Development" : environmentName;
    }

    /// <summary>
    /// Creates a fully-initialized <see cref="AppConfig"/> with all default values set
    /// explicitly, without reading from any external configuration source.
    /// </summary>
    /// <returns>A complete <see cref="AppConfig"/> object graph with documented defaults.</returns>
    /// <remarks>
    /// <para>
    /// This method serves three purposes:
    /// <list type="numbered">
    ///   <item><strong>Documentation</strong> — shows the complete config structure and every default value</item>
    ///   <item><strong>Testing</strong> — provides a baseline config for unit and integration tests</item>
    ///   <item><strong>Fallback</strong> — usable when configuration files are unavailable</item>
    /// </list>
    /// </para>
    /// <para>
    /// All nested objects are instantiated with the same defaults defined in their class constructors.
    /// Modify the returned instance with property setters as needed.
    /// </para>
    /// </remarks>
    public static AppConfig CreateManualAppConfig()
    {
        return new AppConfig
        {
            Common = new()
            {
                ApplicationName = "AgenticHarness",
                ApplicationVersion = "1.0",
                SlowThresholdSec = 5
            },
            Logging = new()
            {
                LogsBasePath = null,
                PipeName = "agentic-harness-logs",
                EnableStructuredJson = true,
                RingBufferCapacity = 500
            },
            Agent = new()
            {
                DefaultRequestTimeoutSec = 30,
                DefaultTokenBudget = 128_000
            },
            Http = new()
            {
                CorsAllowedOrigins = string.Empty,
                Authorization = new() { Enabled = false },
                HttpSwagger = new() { OpenApiEnabled = false }
            },
            Infrastructure = new(),
            Connectors = new(),
            Observability = new()
            {
                WebTelemetryProjects = new List<string> { "Infrastructure.AI.MCPServer" }
            },
            AI = new(),
            Azure = new(),
            Cache = new() { CacheType = CacheType.None }
        };
    }

    /// <summary>
    /// Connects to Azure App Configuration using the connection string stored
    /// in the initial configuration under <c>AzureAppConfigConnectionString</c>.
    /// </summary>
    /// <param name="builder">The configuration builder to add the source to.</param>
    /// <param name="initialConfig">
    /// The bootstrapped configuration containing the App Configuration connection string.
    /// </param>
    /// <remarks>
    /// Keys in Azure App Configuration should follow the <c>AppConfig:Section:Key</c> naming
    /// convention so they bind automatically to <see cref="AppConfig"/> properties.
    /// Two label filters are applied: <c>null</c> (unlabeled) and <c>"API"</c>.
    /// </remarks>
    private static void AddAzureAppConfig(ConfigurationBuilder builder, IConfigurationRoot initialConfig)
    {
        builder.AddAzureAppConfiguration(options =>
        {
            options.Connect(initialConfig["AzureAppConfigConnectionString"])
                .Select(KeyFilter.Any, LabelFilter.Null)
                .Select(KeyFilter.Any, "API");
        });
    }

    /// <summary>
    /// Adds Azure Key Vault as a configuration source using <see cref="DefaultAzureCredential"/>
    /// for authentication.
    /// </summary>
    /// <param name="builder">The configuration builder to add the source to.</param>
    /// <param name="initialConfig">
    /// The bootstrapped configuration containing the Key Vault URI under <c>AzureKeyVaultUri</c>.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <c>AzureKeyVaultUri</c> is not found in <paramref name="initialConfig"/>.
    /// </exception>
    private static void AddAzureKeyVault(ConfigurationBuilder builder, IConfigurationRoot initialConfig)
    {
        var akvUri = initialConfig["AzureKeyVaultUri"]
            ?? throw new ArgumentNullException(
                "AzureKeyVaultUri",
                "Azure Key Vault URI is required but was not found in configuration.");

        builder.AddAzureKeyVault(new Uri(akvUri), new DefaultAzureCredential());
    }
}
