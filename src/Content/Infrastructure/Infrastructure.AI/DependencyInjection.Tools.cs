using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.RAG;
using Application.AI.Common.Interfaces.Tools;
using Azure.AI.Agents.Persistent;
using Azure.AI.OpenAI;
using Application.Common.Factories;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.MetaHarness;
using Infrastructure.AI.Factories;
using Infrastructure.AI.Helpers;
using Infrastructure.AI.Tools;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;

namespace Infrastructure.AI;

public static partial class DependencyInjection
{
    /// <summary>
    /// Registers tool implementations (file system, document, echo) and AI client
    /// connections (Azure OpenAI, OpenAI, AI Inference).
    /// </summary>
    private static void RegisterToolServices(
        IServiceCollection services,
        AppConfig appConfig,
        IEnumerable<string> allowedBasePaths)
    {
        // File system service — sandboxed file operations for direct consumption
        services.AddSingleton<IFileSystemService>(sp =>
            new FileSystemService(
                sp.GetRequiredService<ILogger<FileSystemService>>(),
                allowedBasePaths));

        // File system tool — ITool adapter for LLM consumption, registered with keyed DI
        services.AddKeyedSingleton<ITool>(FileSystemTool.ToolName, (sp, _) =>
            new FileSystemTool(sp.GetRequiredService<IFileSystemService>()));

        // Restricted search tool — sandboxed read-only shell commands for the proposer.
        // Always registered; surfaced to the proposer only when EnableShellTool is true.
        services.AddKeyedSingleton<ITool>(RestrictedSearchTool.ToolName, (sp, _) =>
            new RestrictedSearchTool(
                sp.GetRequiredService<IOptionsMonitor<MetaHarnessConfig>>(),
                sp.GetRequiredService<ILogger<RestrictedSearchTool>>()));

        // Document search tool — RAG pipeline search for LLM consumption
        services.AddKeyedSingleton<ITool>(DocumentSearchTool.ToolName, (sp, _) =>
            new DocumentSearchTool(sp.GetRequiredService<IRagOrchestrator>()));

        // Document ingest tool — RAG pipeline ingestion for LLM consumption
        services.AddKeyedSingleton<ITool>(DocumentIngestTool.ToolName, (sp, _) =>
            new DocumentIngestTool(sp.GetRequiredService<IMediator>()));

        // Echo tools — deterministic tools for E2E testing pipeline verification
        services.AddKeyedSingleton<ITool>(EchoLookupTool.ToolName, (_, _) => new EchoLookupTool());
        services.AddKeyedSingleton<ITool>(EchoCalculateTool.ToolName, (_, _) => new EchoCalculateTool());
    }

    /// <summary>
    /// Registers AI client singletons (AzureOpenAIClient, OpenAIClient) based on
    /// the configured <see cref="AIAgentFrameworkClientType"/>.
    /// </summary>
    private static void RegisterAIClients(IServiceCollection services, AppConfig appConfig)
    {
        var framework = appConfig.AI.AgentFramework;
        if (!framework.IsConfigured)
            return;

        switch (framework.ClientType)
        {
            case AIAgentFrameworkClientType.AzureOpenAI:
                if (!string.IsNullOrWhiteSpace(framework.Endpoint)
                    && Uri.TryCreate(framework.Endpoint, UriKind.Absolute, out var aoaiUri))
                {
                    services.AddSingleton(new AzureOpenAIClient(
                        aoaiUri,
                        new Azure.AzureKeyCredential(framework.ApiKey!),
                        AgentFrameworkHelper.GetAzureOpenAIClientOptions()));
                }
                break;

            case AIAgentFrameworkClientType.OpenAI:
                services.AddSingleton(new OpenAIClient(
                    new System.ClientModel.ApiKeyCredential(framework.ApiKey!),
                    AgentFrameworkHelper.GetOpenAIClientOptions()));
                break;

            case AIAgentFrameworkClientType.AzureAIInference:
            case AIAgentFrameworkClientType.Anthropic:
            case AIAgentFrameworkClientType.Echo:
                // No DI registration needed — ChatClientFactory creates the client
                // directly with a custom endpoint and caches it internally.
                break;
        }
    }

    /// <summary>
    /// Registers Azure AI Foundry persistent agents administration client when configured.
    /// </summary>
    private static void RegisterAIFoundryAgents(IServiceCollection services, AppConfig appConfig)
    {
        if (appConfig.AI.AIFoundry.IsConfigured)
        {
            var credential = AzureCredentialFactory.CreateTokenCredential(appConfig.AI.AIFoundry.Entra);
            services.AddSingleton(new PersistentAgentsAdministrationClient(
                appConfig.AI.AIFoundry.ProjectEndpoint, credential));
        }
    }
}
