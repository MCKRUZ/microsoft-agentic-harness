using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.A2A;
using Application.AI.Common.Interfaces.Attestation;
using Application.AI.Common.Interfaces.DriftDetection;
using Application.AI.Common.Interfaces.Learnings;
using Application.AI.Common.Interfaces.MetaHarness;
using Application.AI.Common.Interfaces.Planner;
using Application.AI.Common.Interfaces.Sandbox;
using Application.AI.Common.Interfaces.Skills;
using Application.AI.Common.Interfaces.Memory;
using Application.AI.Common.Interfaces.Traces;
using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Resilience;
using Domain.AI.Planner;
using Domain.AI.Sandbox;
using Infrastructure.AI.Attestation;
using Infrastructure.AI.DriftDetection;
using Infrastructure.AI.Escalation;
using Infrastructure.AI.Learnings;
using Infrastructure.AI.Memory;
using Infrastructure.AI.Persistence;
using Infrastructure.AI.Planner;
using Infrastructure.AI.Planner.StepExecutors;
using Infrastructure.AI.Resilience;
using Infrastructure.AI.Sandbox;
using Infrastructure.AI.Security;
using Infrastructure.AI.Traces;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Agents;
using Application.AI.Common.Interfaces.Compaction;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Config;
using Application.AI.Common.Interfaces.Hooks;
using Application.AI.Common.Interfaces.Permissions;
using Application.AI.Common.Interfaces.Prompts;
using Application.AI.Common.Interfaces.RAG;
using Application.AI.Common.Interfaces.Tools;
using Application.Common.Factories;
using Azure.AI.Agents.Persistent;
using Azure.AI.OpenAI;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.MetaHarness;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Infrastructure.AI.A2A;
using Infrastructure.AI.MetaHarness;
using Infrastructure.AI.Audit;
using Infrastructure.AI.ContentSafety;
using OpenAI;
using Infrastructure.AI.Agents;
using Infrastructure.AI.Governance;
using Infrastructure.AI.Compaction;
using Infrastructure.AI.Compaction.Strategies;
using Infrastructure.AI.Config;
using Infrastructure.AI.Helpers;
using Infrastructure.AI.Factories;
using Infrastructure.AI.Generators;
using Infrastructure.AI.Hooks;
using Infrastructure.AI.Permissions;
using Infrastructure.AI.Prompts;
using Infrastructure.AI.Skills;
using Infrastructure.AI.Prompts.Sections;
using Infrastructure.AI.StateManagement;
using Infrastructure.AI.StateManagement.Checkpoints;
using Infrastructure.AI.Tools;
using MediatR;
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
        // Secret redaction — applied at all persistence boundaries (traces, snapshots, manifests)
        services.AddSingleton<ISecretRedactor, PatternSecretRedactor>();

        // Snapshot builder — captures live harness config into a redacted, hashed snapshot
        services.AddSingleton<ISnapshotBuilder, ActiveConfigSnapshotBuilder>();

        // Candidate repository — filesystem-backed persistence with atomic writes and JSONL index
        services.AddSingleton<IHarnessCandidateRepository, FileSystemHarnessCandidateRepository>();

        // Execution trace store — filesystem-backed per-run trace artifact persistence
        services.AddSingleton<IExecutionTraceStore, FileSystemExecutionTraceStore>();

        // AI client registration — AzureOpenAIClient or OpenAIClient based on config
        RegisterAIClients(services, appConfig);

        // Resolve relative paths from the exe directory (AppContext.BaseDirectory), not CWD.
        // This ensures appsettings entries like "../../../../../../.." navigate correctly
        // from bin/Debug/net10.0/ up to the repository root regardless of launch CWD.
        var exeDir = AppContext.BaseDirectory;
        var allowedBasePaths = appConfig.Infrastructure.FileSystem.AllowedBasePaths
            .Select(p => Path.IsPathRooted(p) ? p : Path.GetFullPath(p, exeDir))
            .Append(appConfig.Logging.LogsBasePath is { Length: > 0 } lp
                ? Path.GetFullPath(lp, exeDir)
                : string.Empty)
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct();

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

        // Azure AI Foundry persistent agents — register administration client when configured
        if (appConfig.AI.AIFoundry.IsConfigured)
        {
            var credential = AzureCredentialFactory.CreateTokenCredential(appConfig.AI.AIFoundry.Entra);
            services.AddSingleton(new PersistentAgentsAdministrationClient(
                appConfig.AI.AIFoundry.ProjectEndpoint, credential));
        }

        // Chat client factory — creates IChatClient from Azure OpenAI / OpenAI / AI Inference / Persistent Agents
        services.AddSingleton<IChatClientFactory, ChatClientFactory>();

        // Content safety — pass-through logging implementation for POC
        services.AddSingleton<ITextContentSafetyService, StructuredLogContentSafetyService>();

        // Audit — structured log sink for compliance traceability
        services.AddSingleton<IAuditSink, StructuredLogAuditSink>();

        // Skill metadata registry — filesystem discovery via FileAgentSkillLoader
        services.AddSingleton<SkillMetadataParser>();
        services.AddSingleton<ISkillMetadataRegistry, SkillMetadataRegistry>();

        // Agent metadata registry — filesystem discovery of AGENT.md manifests
        services.AddSingleton<AgentMetadataParser>();
        services.AddSingleton<IAgentMetadataRegistry, AgentMetadataRegistry>();

        // Skill content provider — default filesystem implementation for normal agent runs
        // CandidateSkillContentProvider is NOT registered here; the evaluator constructs it
        // directly with a HarnessCandidate snapshot for candidate-isolated evaluation.
        services.AddTransient<ISkillContentProvider, FileSystemSkillContentProvider>();

        // Batched tool execution — parallel reads, serial writes
        services.AddSingleton<IToolConcurrencyClassifier, ToolConcurrencyClassifier>();
        services.AddTransient<IToolExecutionStrategy, BatchedToolExecutionStrategy>();

        // State management — markdown generator, JSON checkpoint manager, composite manager
        services.AddSingleton<IStateMarkdownGenerator, StateMarkdownGenerator>();
        services.AddSingleton<JsonCheckpointStateManager>();
        services.AddSingleton<CompositeStateManager>();

        // A2A protocol — agent-to-agent communication
        services.AddSingleton<IA2AAgentHost, A2AAgentHost>();

        // Permission system — 3-phase resolution with denial tracking
        services.AddSingleton<IPatternMatcher, GlobPatternMatcher>();
        services.AddSingleton<ISafetyGateRegistry, SafetyGateRegistry>();
        services.AddSingleton<IPermissionRuleProvider, ConfigBasedRuleProvider>();
        services.AddSingleton<IDenialTracker, InMemoryDenialTracker>();
        services.AddSingleton<IToolPermissionService, ThreePhasePermissionResolver>();

        // Hook system — in-memory registry and composite executor
        services.AddSingleton<IHookRegistry, InMemoryHookRegistry>();
        services.AddTransient<IHookExecutor, CompositeHookExecutor>();

        // System prompt composer — memoized section-based composition
        services.AddSingleton<IPromptSectionCache, InMemoryPromptSectionCache>();
        services.AddSingleton<ISystemPromptComposer, MemoizedPromptComposer>();
        services.AddTransient<IPromptSectionProvider, AgentIdentitySectionProvider>();
        services.AddTransient<IPromptSectionProvider, ToolSchemasSectionProvider>();
        services.AddTransient<IPromptSectionProvider, PermissionRulesSectionProvider>();
        services.AddTransient<IPromptSectionProvider, SessionStateSectionProvider>();

        // Prompt cache break detection
        services.AddSingleton<IPromptCacheTracker, Sha256PromptCacheTracker>();

        // Context compaction system — strategy executors, circuit breaker, orchestrator
        services.AddSingleton<IAutoCompactStateMachine, AutoCompactStateMachine>();
        services.AddSingleton<IContextCompactionService, ContextCompactionService>();
        services.AddTransient<ICompactionStrategyExecutor, FullCompactionStrategy>();
        services.AddTransient<ICompactionStrategyExecutor, PartialCompactionStrategy>();
        services.AddTransient<ICompactionStrategyExecutor, MicroCompactionStrategy>();

        // Subagent orchestration — tool scoping, messaging, profiles
        services.AddSingleton<ISubagentToolResolver, SubagentToolResolver>();
        services.AddSingleton<IAgentMailbox, InMemoryAgentMailbox>();
        services.AddSingleton<ISubagentProfileRegistry, BuiltInSubagentProfiles>();

        // Autonomy tier resolution — reads tier from SubagentDefinition or falls back to config
        services.AddSingleton<IAutonomyTierResolver, DefaultAutonomyTierResolver>();

        // Delegation persistence — append-only JSONL per supervisor session
        services.AddSingleton<IDelegationStore, JsonlDelegationStore>();

        // Supervisor strategy — deterministic capability-based agent selection, keyed for extensibility
        services.AddKeyedSingleton<ISupervisorStrategy>("capability-match", (sp, _) =>
            new CapabilityMatchStrategy(sp.GetRequiredService<IOptionsMonitor<AppConfig>>()));

        // Supervisor — coordinates delegation, concurrency, depth tracking, and audit
        services.AddSingleton<ISupervisor, CapabilityMatchSupervisor>();

        // Config discovery — directory walk with @include support
        services.AddTransient<IConfigDiscoveryService, DirectoryWalkConfigDiscovery>();

        // Agent history store factory — creates a JsonlAgentHistoryStore for a given execution run.
        // IAgentHistoryStore instances are run-scoped (one per ITraceWriter) and created by
        // AgentExecutionContextFactory (section 14), not by the DI container directly.
        // Register the factory delegate for use by the context factory.
        services.AddSingleton<Func<ITraceWriter, IAgentHistoryStore>>(
            _ => tw => new JsonlAgentHistoryStore(tw));

        // Harness proposer — scoped because each invocation carries iteration-specific context
        services.AddScoped<IHarnessProposer, OrchestratedHarnessProposer>();

        // Harness evaluator — scoped; each evaluation creates its own SemaphoreSlim
        services.AddScoped<IEvaluationService, AgentEvaluationService>();

        // Regression suite service — scoped to match evaluation lifecycle
        services.AddScoped<IRegressionSuiteService, FileSystemRegressionSuiteService>();

        RegisterEscalationServices(services);
        RegisterResilienceServices(services, appConfig);
        RegisterDriftDetectionServices(services);
        RegisterLearningsServices(services, appConfig);

        RegisterPlannerDbContext(services, appConfig);
        RegisterPlannerServices(services);
        RegisterSandboxServices(services);

        return services;
    }

    /// <summary>
    /// Registers escalation pipeline services: service, audit store, composite notifier,
    /// and no-op notification channel stubs.
    /// </summary>
    private static void RegisterEscalationServices(IServiceCollection services)
    {
        services.AddSingleton<IEscalationService, DefaultEscalationService>();
        services.AddSingleton<IEscalationAuditStore, JsonlEscalationAuditStore>();
        services.AddSingleton<IEscalationNotifier, CompositeEscalationNotifier>();
        services.AddSingleton<IEscalationNotificationChannel, NoOpSlackNotifier>();
        services.AddSingleton<IEscalationNotificationChannel, NoOpTeamsNotifier>();
        services.AddSingleton<IEscalationNotificationChannel, DriftEscalationBridge>();
    }

    /// <summary>
    /// Registers resilience pipeline services: health monitor, capability registry,
    /// resilient provider, and conditionally the retry queue hosted service.
    /// </summary>
    private static void RegisterResilienceServices(IServiceCollection services, AppConfig appConfig)
    {
        services.AddSingleton<PollyProviderHealthMonitor>();
        services.AddSingleton<IProviderHealthMonitor>(sp => sp.GetRequiredService<PollyProviderHealthMonitor>());
        services.AddSingleton<ProviderCapabilityRegistry>();
        services.AddSingleton<IResilientChatClientProvider, ResilientChatClientProvider>();

        if (appConfig.AI.Resilience.Enabled)
        {
            services.AddSingleton<LlmRetryQueue>();
            services.AddSingleton<ILlmRetryQueue>(sp => sp.GetRequiredService<LlmRetryQueue>());
            services.AddHostedService(sp => sp.GetRequiredService<LlmRetryQueue>());
        }
    }

    /// <summary>
    /// Registers drift detection pipeline: scorer, baseline store, audit, notifier, EWMA state,
    /// and the main detection service.
    /// </summary>
    private static void RegisterDriftDetectionServices(IServiceCollection services)
    {
        services.AddKeyedSingleton<IDriftScorer>("ewma", (sp, _) =>
            new EwmaDriftScorer(
                sp.GetRequiredService<IEwmaStateStore>(),
                sp.GetRequiredService<IOptionsMonitor<AppConfig>>(),
                sp.GetService<TimeProvider>() ?? TimeProvider.System,
                sp.GetRequiredService<ILogger<EwmaDriftScorer>>()));

        services.AddKeyedSingleton<IDriftBaselineStore>("graph", (sp, _) =>
            new GraphDriftBaselineStore(
                sp.GetRequiredService<Application.AI.Common.Interfaces.KnowledgeGraph.IKnowledgeGraphStore>(),
                sp.GetRequiredService<ILogger<GraphDriftBaselineStore>>()));

        services.AddKeyedSingleton<IDriftBaselineStore>("in_memory", (_, _) =>
            new InMemoryDriftBaselineStore());

        // Default to graph — drift baselines require persistent storage for EWMA continuity
        services.AddSingleton<IDriftBaselineStore>(sp =>
            sp.GetRequiredKeyedService<IDriftBaselineStore>("graph"));

        services.AddSingleton<IDriftAuditStore>(sp =>
            new JsonlDriftAuditStore(
                sp.GetRequiredService<IOptionsMonitor<AppConfig>>(),
                sp.GetService<TimeProvider>() ?? TimeProvider.System,
                sp.GetRequiredService<ILogger<JsonlDriftAuditStore>>()));

        services.AddSingleton<IDriftNotifier, CompositeDriftNotifier>();

        services.AddSingleton<IEwmaStateStore>(sp =>
            new GraphEwmaStateStore(
                sp.GetRequiredService<Application.AI.Common.Interfaces.KnowledgeGraph.IKnowledgeGraphStore>(),
                sp.GetRequiredService<ILogger<GraphEwmaStateStore>>()));

        services.AddSingleton<IDriftDetectionService>(sp =>
            new DefaultDriftDetectionService(
                sp.GetRequiredKeyedService<IDriftScorer>("ewma"),
                sp.GetRequiredService<IDriftBaselineStore>(),
                sp.GetRequiredService<IDriftAuditStore>(),
                sp.GetRequiredService<IDriftNotifier>(),
                sp.GetRequiredService<IEscalationService>(),
                sp.GetRequiredService<Application.AI.Common.Interfaces.KnowledgeGraph.IKnowledgeGraphStore>(),
                sp.GetRequiredService<IOptionsMonitor<AppConfig>>(),
                sp.GetService<TimeProvider>() ?? TimeProvider.System,
                sp.GetRequiredService<ILogger<DefaultDriftDetectionService>>()));
    }

    /// <summary>
    /// Registers learnings subsystem: decay service, drift bridge, and conditional
    /// pruning background service.
    /// </summary>
    private static void RegisterLearningsServices(IServiceCollection services, AppConfig appConfig)
    {
        services.AddSingleton<ILearningDecayService>(sp =>
            new DefaultLearningDecayService(
                sp.GetRequiredService<ILearningsStore>(),
                sp.GetRequiredService<IOptionsMonitor<Domain.Common.Config.AI.Learnings.LearningsConfig>>(),
                sp.GetService<TimeProvider>() ?? TimeProvider.System,
                sp.GetRequiredService<ILogger<DefaultLearningDecayService>>()));

        services.AddSingleton<ILearningsDriftBridge, LearningsDriftBridge>();

        if (appConfig.AI.Learnings.Enabled)
        {
            services.AddSingleton<LearningsPruningBackgroundService>();
            services.AddHostedService(sp => sp.GetRequiredService<LearningsPruningBackgroundService>());
        }
    }

    private static void RegisterPlannerDbContext(IServiceCollection services, AppConfig appConfig)
    {
        var dbPath = appConfig.AI.Planner.DatabasePath;
        var dataDir = Path.GetDirectoryName(Path.Combine(AppContext.BaseDirectory, dbPath))!;
        Directory.CreateDirectory(dataDir);
        var connectionString = $"DataSource={Path.Combine(AppContext.BaseDirectory, dbPath)}";

        services.AddDbContextFactory<PlannerDbContext>(options => options.UseSqlite(connectionString));
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<PlannerDbContext>>().CreateDbContext());
    }

    private static void RegisterPlannerServices(IServiceCollection services)
    {
        services.AddScoped<IPlanExecutor, PlanExecutor>();
        services.AddScoped<IPlanValidator, PlanValidator>();
        services.AddScoped<IPlanGenerator, LlmPlanGeneratorService>();
        services.AddScoped<IPlanStateStore, EfCorePlanStateStore>();
        services.AddScoped<PlanExecutionContext>();

        services.AddKeyedScoped<IPlanStepExecutor>(StepType.LlmCall,
            (sp, _) => sp.GetRequiredService<LlmCallStepExecutor>());
        services.AddKeyedScoped<IPlanStepExecutor>(StepType.ToolUse,
            (sp, _) => sp.GetRequiredService<ToolUseStepExecutor>());
        services.AddKeyedScoped<IPlanStepExecutor>(StepType.HumanGate,
            (sp, _) => sp.GetRequiredService<HumanGateStepExecutor>());
        services.AddKeyedScoped<IPlanStepExecutor>(StepType.ConditionalBranch,
            (sp, _) => sp.GetRequiredService<ConditionalBranchStepExecutor>());
        services.AddKeyedScoped<IPlanStepExecutor>(StepType.SubPlanInvocation,
            (sp, _) => sp.GetRequiredService<SubPlanStepExecutor>());

        services.AddScoped<LlmCallStepExecutor>();
        services.AddScoped<ToolUseStepExecutor>();
        services.AddScoped<HumanGateStepExecutor>();
        services.AddScoped<ConditionalBranchStepExecutor>();
        services.AddScoped<SubPlanStepExecutor>();
    }

    private static void RegisterSandboxServices(IServiceCollection services)
    {
        services.AddKeyedScoped<ISandboxExecutor>(SandboxIsolationLevel.Process,
            (sp, _) => sp.GetRequiredService<ProcessSandboxExecutor>());
        services.AddKeyedScoped<ISandboxExecutor>(SandboxIsolationLevel.Container,
            (sp, _) => sp.GetRequiredService<DockerSandboxExecutor>());

        services.AddScoped<ProcessSandboxExecutor>();
        services.AddScoped<DockerSandboxExecutor>();

        services.AddSingleton<Docker.DotNet.IDockerClient>(_ =>
            new Docker.DotNet.DockerClientConfiguration().CreateClient());

        services.AddScoped<IAttestationService, HmacAttestationService>();

        if (OperatingSystem.IsWindows())
            services.AddSingleton<IProcessResourceLimiter, WindowsProcessResourceLimiter>();
        else
            services.AddSingleton<IProcessResourceLimiter, NoOpProcessResourceLimiter>();
    }

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
}
