using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Observability;
using Application.AI.Common.Interfaces.RAG;
using Application.AI.Common.Interfaces.Tools;
using Azure.AI.Agents.Persistent;
using Azure.AI.OpenAI;
using Azure.AI.Projects;
using Application.Common.Factories;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.MetaHarness;
using Infrastructure.AI.Embeddings;
using Infrastructure.AI.Factories;
using Infrastructure.AI.Helpers;
using Infrastructure.AI.Tools;
using MediatR;
using Microsoft.Extensions.AI;
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

        // Delegation tool — lets a skill hand a self-contained subtask to the capability-matching
        // supervisor, which selects, runs, and governs (autonomy tiers, depth limits, audit) a
        // best-fit subagent. Opt-in per skill via SKILL.md allowed-tools.
        services.AddKeyedSingleton<ITool>(DelegateToSubagentTool.ToolName, (sp, _) =>
            new DelegateToSubagentTool(
                sp.GetRequiredService<Application.AI.Common.Interfaces.Agents.ISupervisor>(),
                sp.GetRequiredService<ILogger<DelegateToSubagentTool>>()));

        // Dashboard control tool — acts on the connected dashboard UI (read view, set time range,
        // navigate, refresh) via a mid-run client round-trip through IClientToolBridge. The bridge
        // implementation is supplied by the Presentation host (AG-UI); absent it, the tool fails
        // gracefully ("no client attached"). Opt-in per skill via SKILL.md allowed-tools.
        services.AddKeyedSingleton<ITool>(DashboardControlTool.ToolName, (sp, _) =>
            new DashboardControlTool(sp.GetRequiredService<IClientToolBridge>()));

        // List-metrics tool — enumerates the curated dashboard metric catalog (shared source) so the
        // agent can pick a valid metric. Read-only, non-blocking. Opt-in per skill via allowed-tools.
        services.AddKeyedSingleton<ITool>(ListMetricsTool.ToolName, (sp, _) =>
            new ListMetricsTool(sp.GetRequiredService<IMetricCatalog>()));

        // Render-chart tool — generative UI: the agent renders a chart inline in its answer via the
        // same client round-trip bridge as dashboard_control. The browser draws an existing chart
        // component from a metric and returns a short summary. Opt-in per skill via allowed-tools.
        services.AddKeyedSingleton<ITool>(RenderChartTool.ToolName, (sp, _) =>
            new RenderChartTool(sp.GetRequiredService<IClientToolBridge>()));
    }

    /// <summary>
    /// Registers AI client singletons (AzureOpenAIClient, OpenAIClient) based on
    /// the configured <see cref="AIAgentFrameworkClientType"/> and wires the
    /// embedding generator used by RAG and knowledge-graph features.
    /// </summary>
    /// <remarks>
    /// Embedding registration follows three rules in order:
    /// <list type="number">
    ///   <item>If <c>AppConfig:AI:Embedding</c> is explicitly configured, use it.</item>
    ///   <item>Else if the chat <see cref="AIAgentFrameworkClientType"/> natively supports
    ///   embeddings (AzureOpenAI, OpenAI), reuse the chat client.</item>
    ///   <item>Else register <see cref="UnconfiguredEmbeddingGenerator"/> — DI graph is
    ///   satisfied; RAG calls throw a clear, actionable error at first use.</item>
    /// </list>
    /// </remarks>
    private static void RegisterAIClients(IServiceCollection services, AppConfig appConfig)
    {
        var framework = appConfig.AI.AgentFramework;
        if (framework.IsConfigured)
            RegisterChatClient(services, framework);

        RegisterGenerationStatsClient(services, framework);
        RegisterEmbeddingGenerator(services, appConfig);
    }

    /// <summary>Registers the chat-client SDK type based on the configured provider.</summary>
    private static void RegisterChatClient(IServiceCollection services, AgentFrameworkConfig framework)
    {
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
                // Endpoint is optional: blank → real OpenAI; set → an OpenAI-compatible gateway
                // such as OpenRouter (https://openrouter.ai/api/v1).
                services.AddSingleton(new OpenAIClient(
                    new System.ClientModel.ApiKeyCredential(framework.ApiKey!),
                    AgentFrameworkHelper.GetOpenAIClientOptions(
                        framework.Endpoint, framework.EnablePromptCaching)));
                break;

            case AIAgentFrameworkClientType.AzureAIInference:
            case AIAgentFrameworkClientType.Anthropic:
            case AIAgentFrameworkClientType.Echo:
                // ChatClientFactory creates these directly with a custom endpoint
                // and caches them internally — no shared SDK singleton needed.
                break;
        }
    }

    /// <summary>
    /// Registers <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> per the three
    /// rules documented on <see cref="RegisterAIClients"/>.
    /// </summary>
    private static void RegisterEmbeddingGenerator(IServiceCollection services, AppConfig appConfig)
    {
        var embedding = appConfig.AI.Embedding;
        var framework = appConfig.AI.AgentFramework;
        // Deployment precedence: AI:Embedding:Deployment > AI:Rag:VectorStore:EmbeddingModel.
        // The latter doubles as the index dimensionality contract, so it's never null.
        var deployment = !string.IsNullOrWhiteSpace(embedding.Deployment)
            ? embedding.Deployment!
            : appConfig.AI.Rag.VectorStore.EmbeddingModel;

        if (embedding.IsConfigured)
        {
            RegisterDedicatedEmbeddingProvider(services, embedding, deployment);
            return;
        }

        if (framework.IsConfigured
            && framework.ClientType is AIAgentFrameworkClientType.AzureOpenAI
                                    or AIAgentFrameworkClientType.OpenAI)
        {
            RegisterChatProviderEmbeddings(services, framework.ClientType, deployment);
            return;
        }

        // Fail-fast sentinel: DI graph is valid, RAG calls throw on first use.
        var chatTypeLabel = framework.IsConfigured
            ? framework.ClientType.ToString()
            : "<unconfigured>";
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
            _ => new UnconfiguredEmbeddingGenerator(chatTypeLabel));
    }

    /// <summary>
    /// Builds a dedicated embedding client from <c>AppConfig:AI:Embedding</c> independent of the
    /// chat client registrations. Supported provider types are AzureOpenAI and OpenAI;
    /// other selections throw at startup so misconfiguration is loud, not silent.
    /// </summary>
    private static void RegisterDedicatedEmbeddingProvider(
        IServiceCollection services,
        EmbeddingConfig embedding,
        string deployment)
    {
        switch (embedding.ClientType)
        {
            case AIAgentFrameworkClientType.AzureOpenAI:
                if (string.IsNullOrWhiteSpace(embedding.Endpoint)
                    || !Uri.TryCreate(embedding.Endpoint, UriKind.Absolute, out var aoaiUri))
                {
                    throw new InvalidOperationException(
                        "AppConfig:AI:Embedding:Endpoint must be a valid absolute URI when " +
                        "ClientType=AzureOpenAI.");
                }
                var aoaiClient = new AzureOpenAIClient(
                    aoaiUri,
                    new Azure.AzureKeyCredential(embedding.ApiKey!),
                    AgentFrameworkHelper.GetAzureOpenAIClientOptions());
                services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
                    _ => aoaiClient.GetEmbeddingClient(deployment).AsIEmbeddingGenerator());
                break;

            case AIAgentFrameworkClientType.OpenAI:
                var openAIClient = new OpenAIClient(
                    new System.ClientModel.ApiKeyCredential(embedding.ApiKey!),
                    AgentFrameworkHelper.GetOpenAIClientOptions(embedding.Endpoint));
                services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
                    _ => openAIClient.GetEmbeddingClient(deployment).AsIEmbeddingGenerator());
                break;

            default:
                throw new InvalidOperationException(
                    $"AppConfig:AI:Embedding:ClientType '{embedding.ClientType}' is not a " +
                    "supported embedding provider. Use AzureOpenAI or OpenAI.");
        }
    }

    /// <summary>
    /// Reuses the chat provider's already-registered SDK client to serve embeddings.
    /// Only valid for AzureOpenAI and OpenAI — guarded by the caller.
    /// </summary>
    private static void RegisterChatProviderEmbeddings(
        IServiceCollection services,
        AIAgentFrameworkClientType chatClientType,
        string deployment)
    {
        switch (chatClientType)
        {
            case AIAgentFrameworkClientType.AzureOpenAI:
                services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
                    sp.GetRequiredService<AzureOpenAIClient>()
                      .GetEmbeddingClient(deployment)
                      .AsIEmbeddingGenerator());
                break;

            case AIAgentFrameworkClientType.OpenAI:
                services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
                    sp.GetRequiredService<OpenAIClient>()
                      .GetEmbeddingClient(deployment)
                      .AsIEmbeddingGenerator());
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

            // Foundry Responses agent (direct inference) — AIProjectClient drives the project's
            // Responses API; FoundryAgentProvider builds the non-versioned ChatClientAgent for the
            // FoundryResponses client type. Both gated on the project endpoint being configured.
            services.AddSingleton(new AIProjectClient(
                new Uri(appConfig.AI.AIFoundry.ProjectEndpoint), credential));
            services.AddSingleton<IFoundryAgentProvider, FoundryAgentProvider>();
        }
    }
}
