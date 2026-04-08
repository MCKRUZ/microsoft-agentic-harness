using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.A2A;
using Application.AI.Common.Interfaces.Tools;
using Application.Common.Factories;
using Azure.AI.Agents.Persistent;
using Domain.Common.Config;
using Infrastructure.AI.A2A;
using Infrastructure.AI.Factories;
using Infrastructure.AI.Generators;
using Infrastructure.AI.StateManagement;
using Infrastructure.AI.StateManagement.Checkpoints;
using Infrastructure.AI.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI;

/// <summary>
/// Dependency injection configuration for the Infrastructure.AI layer.
/// Registers tool implementations, service wrappers, AI infrastructure services,
/// and optional Azure AI Foundry persistent agents support.
/// </summary>
/// <remarks>
/// Called from the Presentation composition root after Application dependencies:
/// <code>
/// services.AddApplicationCommonDependencies(appConfig);
/// services.AddApplicationAIDependencies();
/// services.AddInfrastructureAIDependencies(appConfig);
/// </code>
/// </remarks>
public static class DependencyInjection
{
    /// <summary>
    /// Registers all Infrastructure.AI dependencies into the service collection.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="appConfig">
    /// The fully bound application configuration. Used to extract allowed base paths
    /// for the file system service and to configure Azure AI Foundry persistent agents.
    /// </param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddInfrastructureAIDependencies(
        this IServiceCollection services,
        AppConfig appConfig)
    {
        var allowedBasePaths = new[] { appConfig.Logging.LogsBasePath ?? string.Empty }
            .Where(p => !string.IsNullOrEmpty(p));

        // File system service — sandboxed file operations for direct consumption
        services.AddSingleton<IFileSystemService>(sp =>
            new FileSystemService(
                sp.GetRequiredService<ILogger<FileSystemService>>(),
                allowedBasePaths));

        // File system tool — ITool adapter for LLM consumption, registered with keyed DI
        services.AddKeyedSingleton<ITool>(FileSystemTool.ToolName, (sp, _) =>
            new FileSystemTool(sp.GetRequiredService<IFileSystemService>()));

        // Azure AI Foundry persistent agents — register administration client when configured
        if (appConfig.AI.AIFoundry.IsConfigured)
        {
            var credential = AzureCredentialFactory.CreateTokenCredential(appConfig.AI.AIFoundry.Entra);
            services.AddSingleton(new PersistentAgentsAdministrationClient(
                appConfig.AI.AIFoundry.ProjectEndpoint, credential));
        }

        // Chat client factory — creates IChatClient from Azure OpenAI / OpenAI / Persistent Agents
        services.AddSingleton<IChatClientFactory, ChatClientFactory>();

        // State management — markdown generator, JSON checkpoint manager, composite manager
        services.AddSingleton<IStateMarkdownGenerator, StateMarkdownGenerator>();
        services.AddSingleton<JsonCheckpointStateManager>();
        services.AddSingleton<CompositeStateManager>();

        // A2A protocol — agent-to-agent communication
        services.AddSingleton<IA2AAgentHost, A2AAgentHost>();

        return services;
    }
}
