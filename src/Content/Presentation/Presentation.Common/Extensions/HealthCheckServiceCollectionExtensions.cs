using Azure.Storage.Blobs;
using Domain.Common.Config;
using HealthChecks.ApplicationStatus.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Presentation.Common.Extensions;

/// <summary>
/// Extension methods for registering health check probes.
/// Conditionally adds checks for SQL Server, Azure Blob Storage,
/// Azure Key Vault, Redis, and Application Insights based on configuration.
/// </summary>
public static class HealthCheckServiceCollectionExtensions
{
    /// <summary>
    /// Registers health check probes for application status and conditional
    /// checks for SQL Server, Azure Blob Storage, Azure Key Vault, and Redis.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="appConfig">The full application configuration for connection strings.</param>
    /// <param name="includeHealthChecksUI">
    /// When <c>true</c>, registers the HealthChecks UI with in-memory storage.
    /// </param>
    /// <returns>The service collection for chaining.</returns>
    /// <remarks>
    /// External service health checks are only registered when their connection
    /// strings are configured and non-empty. This allows the harness to run in
    /// minimal local mode without Azure dependencies.
    /// </remarks>
    public static IServiceCollection AddCustomHealthChecks(
        this IServiceCollection services,
        AppConfig appConfig,
        bool includeHealthChecksUI = true)
    {
        if (includeHealthChecksUI)
        {
            services
                .AddHealthChecksUI()
                .AddInMemoryStorage();
        }

        var builder = services.AddHealthChecks()
            .AddApplicationStatus();

        AddAzureHealthChecks(builder, appConfig);
        AddCacheHealthChecks(builder, appConfig);
        AddHealthCheckPublishers(builder, appConfig);

        return services;
    }

    /// <summary>
    /// Registers health checks for Azure SQL Server, Blob Storage, and Key Vault
    /// when their connection strings are configured.
    /// </summary>
    private static void AddAzureHealthChecks(
        IHealthChecksBuilder builder,
        AppConfig appConfig)
    {
        var sqlConnectionString = appConfig.Azure.Database.AzureSQLClient.ConnectionString;
        if (!string.IsNullOrEmpty(sqlConnectionString))
        {
            builder.AddSqlServer(
                sqlConnectionString,
                name: "sql-server",
                failureStatus: HealthStatus.Degraded);
        }

        var blobConnectionString = appConfig.Azure.Database.AzureBlobStorage.ConnectionString;
        if (!string.IsNullOrEmpty(blobConnectionString))
        {
            builder.AddAzureBlobStorage(
                sp => new BlobServiceClient(blobConnectionString),
                name: "azure-blob-storage",
                failureStatus: HealthStatus.Degraded);
        }

        var keyVaultUri = appConfig.Azure.KeyVault.VaultUri;
        if (!string.IsNullOrEmpty(keyVaultUri))
        {
            builder.AddAzureKeyVault(
                new Uri(keyVaultUri),
                new Azure.Identity.DefaultAzureCredential(),
                options => { },
                name: "azure-key-vault",
                failureStatus: HealthStatus.Degraded);
        }
    }

    /// <summary>
    /// Registers a Redis health check when a Redis endpoint is configured.
    /// </summary>
    private static void AddCacheHealthChecks(
        IHealthChecksBuilder builder,
        AppConfig appConfig)
    {
        var redisEndpoint = appConfig.Cache.RedisClient.Endpoint;
        if (!string.IsNullOrEmpty(redisEndpoint))
        {
            builder.AddRedis(
                redisEndpoint,
                name: "redis",
                failureStatus: HealthStatus.Degraded);
        }
    }

    /// <summary>
    /// Registers the Application Insights health check publisher when
    /// an Application Insights connection string is configured.
    /// </summary>
    private static void AddHealthCheckPublishers(
        IHealthChecksBuilder builder,
        AppConfig appConfig)
    {
        var appInsightsConnectionString = appConfig.Azure.ApplicationInsights.ConnectionString;
        if (!string.IsNullOrEmpty(appInsightsConnectionString))
        {
            builder.AddApplicationInsightsPublisher(connectionString: appInsightsConnectionString);
        }
    }
}
