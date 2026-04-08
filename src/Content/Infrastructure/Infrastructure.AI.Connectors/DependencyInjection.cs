using Application.AI.Common.Interfaces.Connectors;
using Application.AI.Common.Interfaces.Tools;
using Infrastructure.AI.Connectors.AzureDevOps;
using Infrastructure.AI.Connectors.Core;
using Infrastructure.AI.Connectors.GitHub;
using Infrastructure.AI.Connectors.Jira;
using Infrastructure.AI.Connectors.Slack;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.AI.Connectors;

/// <summary>
/// Dependency injection configuration for AI Connector integrations.
/// Registers all connector clients and the factory for runtime lookup.
/// </summary>
/// <remarks>
/// <para>
/// To add a new connector:
/// <list type="number">
///   <item><description>Create a config class in <c>Domain.Common.Config.Connectors</c></description></item>
///   <item><description>Add the config property to <c>ConnectorsConfig</c></description></item>
///   <item><description>Create the connector class extending <see cref="ConnectorClientBase"/></description></item>
///   <item><description>Register it here as <c>AddSingleton&lt;IConnectorClient, YourConnector&gt;()</c></description></item>
/// </list>
/// </para>
/// </remarks>
public static class DependencyInjection
{
    /// <summary>
    /// Registers AI connector clients and related services.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddAIConnectors(
        this IServiceCollection services)
    {
        // Register connector clients
        services.AddSingleton<IConnectorClient, AzureDevOpsWorkItemsConnector>();
        services.AddSingleton<IConnectorClient, GitHubReposConnector>();
        services.AddSingleton<IConnectorClient, GitHubIssuesConnector>();
        services.AddSingleton<IConnectorClient, JiraIssuesConnector>();
        services.AddSingleton<IConnectorClient, SlackNotificationsConnector>();

        // Register factory for runtime connector lookup (cached dictionary)
        services.AddSingleton<IConnectorClientFactory, ConnectorClientFactory>();

        // Bridge connectors into the ITool pipeline via keyed DI
        // so the agent harness can discover and invoke them like any other tool
        RegisterConnectorAsTool(services, "azure_devops_work_items");
        RegisterConnectorAsTool(services, "github_repos");
        RegisterConnectorAsTool(services, "github_issues");
        RegisterConnectorAsTool(services, "jira_issues");
        RegisterConnectorAsTool(services, "slack_notifications");

        return services;
    }

    private static void RegisterConnectorAsTool(IServiceCollection services, string toolName)
    {
        services.AddKeyedSingleton<ITool>(toolName, (sp, _) =>
            new ConnectorToolAdapter(
                sp.GetRequiredService<IConnectorClientFactory>().GetClient(toolName)
                ?? throw new InvalidOperationException($"Connector '{toolName}' not registered")));
    }
}
