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
using Domain.Common.Config.AI.Governance;
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
        // File system service — sandboxed file operations for direct consumption.
        // The governance-state directory is denied even when it falls inside an allowed base path:
        // it holds approval verdicts, so an agent that could read it could mine approval payloads
        // and one that could write it could truncate or corrupt the harness's own governance state.
        // It could not forge a verdict — every row is HMAC-sealed by
        // AttestationGovernanceRecordSealer and verified fail-closed on read — so the exposure is
        // disclosure and tamper/denial of service. Resolved through the same helper the database
        // registration uses, so the two paths cannot drift apart.
        var governanceStateDirectory = Path.GetDirectoryName(
            Persistence.GovernanceStatePaths.Resolve(appConfig.AI.Governance.DurableState.DatabasePath));

        // Armed only when there is genuinely something to protect — see the helper for why the
        // toggles alone are not the whole condition.
        var protectedPaths = ResolveGovernanceStateProtectedPaths(
            appConfig.AI.Governance.DurableState, governanceStateDirectory);

        services.AddSingleton<IFileSystemService>(sp =>
            new FileSystemService(
                sp.GetRequiredService<ILogger<FileSystemService>>(),
                allowedBasePaths,
                protectedPaths));

        // Boot-time assertion that the governance-state directory does not sit inside an allowed
        // base path. That geometry is a misconfiguration worth refusing on, but note what it does
        // NOT do: it does not close the hard-link bypass. A hard link needs the same VOLUME as its
        // target, not the same subtree, and the shipped default puts workspace and .agent-state
        // side by side on one volume. FileSystemService's per-operation link-count check is what
        // closes that. Both collections are handed over by value so the assertion covers exactly
        // the paths the service enforces.
        services.AddHostedService(sp => new FileSystemSandboxStartupValidator(
            [.. allowedBasePaths],
            protectedPaths,
            sp.GetRequiredService<ILogger<FileSystemSandboxStartupValidator>>()));

        // File system tool — ITool adapter for LLM consumption, registered with keyed DI
        services.AddKeyedSingleton<ITool>(FileSystemTool.ToolName, (sp, _) =>
            new FileSystemTool(sp.GetRequiredService<IFileSystemService>()));

        // ILocatedArtifactReader implementations (#319 claim verification) — keyed by location
        // scheme, unconditional (neither depends on a judge model, unlike ClaimVerificationRunner's
        // eval-gated IClaimVerifier). Registered here, not in Infrastructure.AI.Evaluation, because
        // gating a plain file read or config lookup behind AddClaimVerification() would be scope
        // creep — see ClaimVerificationDependencyInjection's remarks.
        services.AddKeyedSingleton<Application.AI.Common.Interfaces.ClaimVerification.ILocatedArtifactReader>(
            Domain.AI.ClaimVerification.ClaimLocationScheme.File, (sp, _) => new Verification.Readers.FileSystemLocatedArtifactReader(
                sp.GetRequiredService<IFileSystemService>(),
                sp.GetRequiredService<ILogger<Verification.Readers.FileSystemLocatedArtifactReader>>()));
        services.AddKeyedSingleton<Application.AI.Common.Interfaces.ClaimVerification.ILocatedArtifactReader>(
            Domain.AI.ClaimVerification.ClaimLocationScheme.Config, (sp, _) => new Verification.Readers.ConfigSnapshotLocatedArtifactReader(
                sp.GetRequiredService<IOptionsMonitor<AppConfig>>(),
                sp.GetRequiredService<ILogger<Verification.Readers.ConfigSnapshotLocatedArtifactReader>>()));

        // Restricted search tool — sandboxed read-only shell commands for the proposer.
        // Always registered; surfaced to the proposer only when EnableShellTool is true.
        services.AddKeyedSingleton<ITool>(RestrictedSearchTool.ToolName, (sp, _) =>
            new RestrictedSearchTool(
                sp.GetRequiredService<IOptionsMonitor<MetaHarnessConfig>>(),
                sp.GetRequiredService<ILogger<RestrictedSearchTool>>()));

        // Document search tool — RAG pipeline search for LLM consumption
        services.AddKeyedSingleton<ITool>(DocumentSearchTool.ToolName, (sp, _) =>
            new DocumentSearchTool(
                sp.GetRequiredService<IRagOrchestrator>(),
                sp.GetRequiredService<ILogger<DocumentSearchTool>>()));

        // Document ingest tool — RAG pipeline ingestion for LLM consumption.
        // Takes the scope factory (not IMediator): the mediator pipeline resolves
        // scoped services, so the singleton tool dispatches inside a fresh scope.
        services.AddKeyedSingleton<ITool>(DocumentIngestTool.ToolName, (sp, _) =>
            new DocumentIngestTool(
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<ILogger<DocumentIngestTool>>()));

        // Echo tools — deterministic tools for E2E testing pipeline verification
        services.AddKeyedSingleton<ITool>(EchoLookupTool.ToolName, (_, _) => new EchoLookupTool());
        services.AddKeyedSingleton<ITool>(EchoCalculateTool.ToolName, (_, _) => new EchoCalculateTool());

        // Tool-result retrieval (#521) — Singleton, like every other keyed tool above. Every production
        // caller resolves a keyed ITool by name from a singleton holding the ROOT provider
        // (ToolChainBuilder.ResolveToolByName, FirstPartyToolLookup.Resolve), so there is no per-request
        // scope at resolution time to register INTO — an earlier AddKeyedScoped registration threw
        // InvalidOperationException on every turn of every skill that lists this tool (caught by the
        // correctness gate). The request-scoped IAgentExecutionContext it needs is resolved per
        // invocation from IAmbientRequestScope.Current instead — see ToolResultFetchTool's own remarks.
        services.AddKeyedSingleton<ITool>(ToolResultFetchTool.ToolName, (sp, _) =>
            new ToolResultFetchTool(
                sp.GetRequiredService<Application.AI.Common.Interfaces.Context.IToolResultStore>(),
                sp.GetRequiredService<Application.AI.Common.Interfaces.IAmbientRequestScope>(),
                sp.GetRequiredService<IOptionsMonitor<Domain.Common.Config.AppConfig>>(),
                sp.GetRequiredService<ILogger<ToolResultFetchTool>>()));

        // Delegation tool — lets a skill hand a self-contained subtask to the capability-matching
        // supervisor, which selects, runs, and governs (autonomy tiers, depth limits, audit) a
        // best-fit subagent. Opt-in per skill via SKILL.md allowed-tools.
        services.AddKeyedSingleton<ITool>(DelegateToSubagentTool.ToolName, (sp, _) =>
            new DelegateToSubagentTool(
                sp.GetRequiredService<Application.AI.Common.Interfaces.Agents.ISupervisor>(),
                sp.GetRequiredService<Application.AI.Common.Interfaces.IAmbientRequestScope>(),
                sp.GetRequiredService<ILogger<DelegateToSubagentTool>>()));

        // Dashboard control tool — acts on the connected dashboard UI (read view, set time range,
        // navigate, refresh) via a mid-run client round-trip through IClientToolBridge. The bridge
        // implementation is supplied by the Presentation host (AG-UI); absent it, the tool fails
        // gracefully ("no client attached"). Opt-in per skill via SKILL.md allowed-tools.
        services.AddKeyedSingleton<ITool>(DashboardControlTool.ToolName, (sp, _) =>
            new DashboardControlTool(
                sp.GetRequiredService<IClientToolBridge>(),
                sp.GetRequiredService<ILogger<DashboardControlTool>>()));

        // List-metrics tool — enumerates the curated dashboard metric catalog (shared source) so the
        // agent can pick a valid metric. Read-only, non-blocking. Opt-in per skill via allowed-tools.
        services.AddKeyedSingleton<ITool>(ListMetricsTool.ToolName, (sp, _) =>
            new ListMetricsTool(sp.GetRequiredService<IMetricCatalog>()));

        // Render-chart tool — generative UI: the agent renders a chart inline in its answer via the
        // same client round-trip bridge as dashboard_control. The browser draws an existing chart
        // component from a metric and returns a short summary. Opt-in per skill via allowed-tools.
        services.AddKeyedSingleton<ITool>(RenderChartTool.ToolName, (sp, _) =>
            new RenderChartTool(
                sp.GetRequiredService<IClientToolBridge>(),
                sp.GetRequiredService<ILogger<RenderChartTool>>()));

        // Render-image tool — generative UI: the agent displays an image inline in its answer via the
        // same client round-trip bridge. The browser renders an <img> from a validated https URL and
        // returns a short acknowledgement. General-purpose (not dashboard-specific); opt-in per skill.
        services.AddKeyedSingleton<ITool>(RenderImageTool.ToolName, (sp, _) =>
            new RenderImageTool(
                sp.GetRequiredService<IClientToolBridge>(),
                sp.GetRequiredService<ILogger<RenderImageTool>>()));

        // Render-form tool — generative UI: the agent displays an interactive form inline. The browser
        // acknowledges display synchronously; the user's answers arrive later as an ordinary next
        // message (not through this tool). General-purpose; opt-in per skill.
        services.AddKeyedSingleton<ITool>(RenderFormTool.ToolName, (sp, _) =>
            new RenderFormTool(
                sp.GetRequiredService<IClientToolBridge>(),
                sp.GetRequiredService<ILogger<RenderFormTool>>()));

        // Render-table tool — generative UI: the agent displays a data table inline via the same client
        // round-trip bridge. The browser draws a table from validated columns/rows and returns a short
        // acknowledgement synchronously (non-interactive). General-purpose; opt-in per skill.
        services.AddKeyedSingleton<ITool>(RenderTableTool.ToolName, (sp, _) =>
            new RenderTableTool(
                sp.GetRequiredService<IClientToolBridge>(),
                sp.GetRequiredService<ILogger<RenderTableTool>>()));
    }

    /// <summary>
    /// Decides whether the governance-state directory is handed to <see cref="FileSystemService"/>
    /// as a protected path, and returns it in the (at most one element) form the service and its
    /// startup validator both receive.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is gated at all.</b> Arming the deny list also arms
    /// <see cref="FileSystemService"/>'s per-operation hard-link identity check, and that check
    /// fails closed on any platform <see cref="HardLinkInspector"/> has no implementation for.
    /// Registering the directory unconditionally therefore made the file-system tool unusable on
    /// macOS and the BSDs under <em>every</em> configuration — including the shipped default, where
    /// both durable-state toggles are off and no database has ever existed. A control armed for a
    /// feature that is not running is cost with no matching benefit.
    /// </para>
    /// <para>
    /// <b>Why the toggles alone are not the condition.</b> A database left behind by an earlier run
    /// with durability enabled still holds approval verdicts after someone sets the toggles back to
    /// false. Gating on the toggles alone would silently un-protect that residue, so an existing
    /// directory arms the protection on its own. The probe is a single
    /// <see cref="Directory.Exists"/> call at composition — once per host, never per file operation.
    /// </para>
    /// <para>
    /// <b>Why a composition-time snapshot is sound.</b> The directory can only come into existence
    /// on a host whose composition-time toggles were already on. Every route to
    /// <c>GovernanceStatePaths.EnsureDirectory</c> runs inside the <c>GovernanceStateDbContext</c>
    /// options callback, which is reached only by resolving <c>EfCoreEscalationStateStore</c>,
    /// <c>EfCoreChangeProposalStore</c>, or <c>GovernanceStatePruner</c>: the first two are selected
    /// by a toggle read once at first resolution, and the third sits behind
    /// <c>EscalationReconciliationService</c>'s construction-time enable snapshot. Nothing re-reads
    /// the toggles afterwards, so no live <c>appsettings.json</c> edit can conjure a database that
    /// this decision did not see. That last property is load-bearing — if the pruner ever goes back
    /// to reading <see cref="Microsoft.Extensions.Options.IOptionsMonitor{T}"/> per tick, a host
    /// that booted with nothing to protect could grow a database behind a disarmed deny list.
    /// </para>
    /// </remarks>
    /// <param name="durableState">The durable governance-state configuration.</param>
    /// <param name="governanceStateDirectory">
    /// The resolved directory holding the governance-state database. Never <see langword="null"/>
    /// in practice — <c>GovernanceStatePaths.Resolve</c> rejects a path with no containing
    /// directory — so the null case is here to satisfy nullable analysis, not to describe a
    /// reachable configuration.
    /// </param>
    /// <returns>
    /// A single-element array holding the directory when protecting it is meaningful; otherwise an
    /// empty array, which leaves both the deny list and the hard-link check disarmed.
    /// </returns>
    internal static string[] ResolveGovernanceStateProtectedPaths(
        GovernanceDurableStateConfig durableState,
        string? governanceStateDirectory)
    {
        ArgumentNullException.ThrowIfNull(durableState);

        if (string.IsNullOrWhiteSpace(governanceStateDirectory))
            return [];

        if (durableState.EscalationsEnabled || durableState.ChangeProposalsEnabled ||
            durableState.CallOnceEnforcementEnabled)
            return [governanceStateDirectory];

        return Directory.Exists(governanceStateDirectory) ? [governanceStateDirectory] : [];
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
    /// <remarks>
    /// Each provider is registered twice: the default client, whose SDK-level retry stays on
    /// because most callers have nothing wrapping them, and a keyed
    /// <see cref="AgentFrameworkHelper.NoProviderRetryClientKey"/> variant with that retry
    /// disabled, resolved only by the provider fallback chain so the Polly pipeline is the sole
    /// retry authority there. The keyed variants use the factory overload so they are built
    /// lazily — a host that never enables resilience never constructs them.
    /// </remarks>
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

                    services.AddKeyedSingleton(
                        AgentFrameworkHelper.NoProviderRetryClientKey,
                        (_, _) => new AzureOpenAIClient(
                            aoaiUri,
                            new Azure.AzureKeyCredential(framework.ApiKey!),
                            AgentFrameworkHelper.GetAzureOpenAIClientOptions(disableProviderRetry: true)));
                }
                break;

            case AIAgentFrameworkClientType.OpenAI:
                // Endpoint is optional: blank → real OpenAI; set → an OpenAI-compatible gateway
                // such as OpenRouter (https://openrouter.ai/api/v1).
                services.AddSingleton(new OpenAIClient(
                    new System.ClientModel.ApiKeyCredential(framework.ApiKey!),
                    AgentFrameworkHelper.GetOpenAIClientOptions(
                        framework.Endpoint, framework.EnablePromptCaching)));

                services.AddKeyedSingleton(
                    AgentFrameworkHelper.NoProviderRetryClientKey,
                    (_, _) => new OpenAIClient(
                        new System.ClientModel.ApiKeyCredential(framework.ApiKey!),
                        AgentFrameworkHelper.GetOpenAIClientOptions(
                            framework.Endpoint, framework.EnablePromptCaching, disableProviderRetry: true)));
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
    /// Registers Azure AI Foundry persistent agents administration client when configured, and the
    /// direct-endpoint Responses client independently (issue #382).
    /// </summary>
    private static void RegisterAIFoundryAgents(IServiceCollection services, AppConfig appConfig)
    {
        var foundry = appConfig.AI.AIFoundry;

        if (!foundry.IsConfigured && !foundry.IsDirectResponsesConfigured)
            return;

        // Shared across both branches below: same resource, same tenant, same Entra credential
        // either way (AIFoundryConfig.Entra's own doc), so a consumer configuring both
        // ProjectEndpoint and ResourceEndpoint gets one credential chain, not two.
        var credential = AzureCredentialFactory.CreateTokenCredential(foundry.Entra);

        if (foundry.IsConfigured)
        {
            services.AddSingleton(new PersistentAgentsAdministrationClient(
                foundry.ProjectEndpoint, credential));

            // Foundry Responses agent (direct inference) — AIProjectClient drives the project's
            // Responses API; FoundryAgentProvider builds the non-versioned ChatClientAgent for the
            // FoundryResponses client type. Both gated on the project endpoint being configured.
            services.AddSingleton(new AIProjectClient(
                new Uri(foundry.ProjectEndpoint), credential));
            services.AddSingleton<IFoundryAgentProvider, FoundryAgentProvider>();
        }

        if (foundry.IsDirectResponsesConfigured)
        {
            // FoundryDirectResponses (issue #382): bypasses AIProjectClient's Project-scoped
            // routing — measured ~15x slower than calling the same model/tenant/credential via the
            // bare resource endpoint — using a plain AzureOpenAIClient against ResourceEndpoint.
            // Gated independently of IsConfigured/ProjectEndpoint: a consumer may configure only
            // this direct path, only the Project-scoped path, or both.
            if (!Uri.TryCreate(foundry.ResourceEndpoint, UriKind.Absolute, out var resourceUri))
            {
                throw new InvalidOperationException(
                    $"AppConfig:AI:AIFoundry:ResourceEndpoint '{foundry.ResourceEndpoint}' is not a " +
                    "valid absolute URI.");
            }

            services.AddKeyedSingleton(
                AgentFrameworkHelper.FoundryDirectResponsesClientKey,
                (_, _) => new AzureOpenAIClient(
                    resourceUri, credential, AgentFrameworkHelper.GetAzureOpenAIClientOptions()));

            services.AddKeyedSingleton(
                AgentFrameworkHelper.FoundryDirectResponsesNoRetryClientKey,
                (_, _) => new AzureOpenAIClient(
                    resourceUri, credential,
                    AgentFrameworkHelper.GetAzureOpenAIClientOptions(disableProviderRetry: true)));
        }
    }
}
