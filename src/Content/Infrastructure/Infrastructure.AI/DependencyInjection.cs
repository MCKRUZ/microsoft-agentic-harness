using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Compression;
using Application.AI.Common.Interfaces.Agents;
using Application.AI.Common.Interfaces.Compaction;
using Application.AI.Common.Interfaces.Config;
using Application.AI.Common.Interfaces.Context;
using Application.AI.Common.Interfaces.Hooks;
using Application.AI.Common.Interfaces.MetaHarness;
using Application.AI.Common.Interfaces.Plugins;
using Application.AI.Common.Interfaces.Prompts;
using Application.AI.Common.Interfaces.Routing;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Interfaces.Traces;
using Application.Common.Factories;
using Domain.Common.Config;
using Infrastructure.AI.Agents;
using Infrastructure.AI.Audit;
using Infrastructure.AI.Compaction;
using Infrastructure.AI.Compaction.Strategies;
using Infrastructure.AI.Tools.GitOps;
using Infrastructure.AI.Tools.Workspace;
using Infrastructure.AI.Compression;
using Infrastructure.AI.Compression.Strategies;
using Infrastructure.AI.Config;
using Infrastructure.AI.ContentSafety;
using Infrastructure.AI.Factories;
using Infrastructure.AI.Generators;
using Infrastructure.AI.Hooks;
using Infrastructure.AI.MetaHarness;
using Infrastructure.AI.Plugins;
using Infrastructure.AI.Prompts;
using Infrastructure.AI.Prompts.Sections;
using Infrastructure.AI.Security;
using Infrastructure.AI.Skills;
using Infrastructure.AI.StateManagement;
using Infrastructure.AI.StateManagement.Checkpoints;
using Infrastructure.AI.Routing;
using Infrastructure.AI.Tools;
using Infrastructure.AI.Traces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI;

/// <summary>
/// Dependency injection configuration for the Infrastructure.AI layer.
/// Registers tool implementations, service wrappers, AI infrastructure services,
/// and optional Azure AI Foundry persistent agents support.
/// </summary>
/// <remarks>
/// <para>
/// This is a partial class split across multiple files by concern:
/// <list type="bullet">
///   <item><description><c>DependencyInjection.cs</c> — entry point and core services</description></item>
///   <item><description><c>DependencyInjection.Tools.cs</c> — tool registrations and AI client setup</description></item>
///   <item><description><c>DependencyInjection.Governance.cs</c> — permissions, escalation, resilience</description></item>
///   <item><description><c>DependencyInjection.Planner.cs</c> — planner DB, step executors, sandbox</description></item>
///   <item><description><c>DependencyInjection.Quality.cs</c> — drift detection and learnings</description></item>
///   <item><description><c>DependencyInjection.Egress.cs</c> — per-skill egress policy + AntiSSRF</description></item>
///   <item><description><c>DependencyInjection.IncidentResponse.cs</c> — incident-response plan registry + ambient context</description></item>
/// </list>
/// </para>
/// Called from the Presentation composition root after Application dependencies:
/// <code>
/// services.AddApplicationCommonDependencies(appConfig);
/// services.AddApplicationAIDependencies();
/// services.AddInfrastructureAIDependencies(appConfig);
/// </code>
/// </remarks>
public static partial class DependencyInjection
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
        // --- Core cross-cutting services ---

        // Secret redaction — applied at all persistence boundaries (traces, snapshots, manifests)
        services.AddSingleton<ISecretRedactor, PatternSecretRedactor>();

        // Snapshot builder — captures live harness config into a redacted, hashed snapshot
        services.AddSingleton<ISnapshotBuilder, ActiveConfigSnapshotBuilder>();

        // Candidate repository — filesystem-backed persistence with atomic writes and JSONL index
        services.AddSingleton<IHarnessCandidateRepository, FileSystemHarnessCandidateRepository>();

        // Execution trace store — filesystem-backed per-run trace artifact persistence
        services.AddSingleton<IExecutionTraceStore, FileSystemExecutionTraceStore>();

        // Tool result store — used by ToolOutputCompressionBehavior to off-load large
        // tool results so they don't dominate the context window. Filesystem-backed by
        // default; consumers can override with a different IToolResultStore after this call.
        services.AddSingleton<IToolResultStore, Context.FileSystemToolResultStore>();

        // --- Tools and AI clients ---

        RegisterAIClients(services, appConfig);
        RegisterAIFoundryAgents(services, appConfig);

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

        RegisterToolServices(services, appConfig, allowedBasePaths);

        // Workspace skill pack — read_file, write_file (submits ChangeProposal), list_files,
        // run_tests, run_lint. Ambient IWorkspaceContextAccessor flows the sandbox-injected
        // working-copy path. Registered unconditionally so any consumer that enables the
        // workspace-skill plugin sees the keyed-DI tool entries available.
        services.AddWorkspaceSkillTools();

        // GitOps skill pack — detect_drift, cluster_health, propose_remediation (submits
        // ChangeProposal), k8sgpt_analyze. Flux + Argo CD controllers behind IGitOpsController;
        // the active one is selected by AppConfig.AI.GitOps.ActiveController. Registered
        // unconditionally; GitOpsStartupValidator fails the host loud when Enabled with bad config.
        services.AddGitOpsSkillTools();

        // Chat client factory — creates IChatClient from Azure OpenAI / OpenAI / AI Inference / Persistent Agents
        services.AddSingleton<IChatClientFactory, ChatClientFactory>();

        // --- Content safety and audit ---

        services.AddSingleton<ITextContentSafetyService, StructuredLogContentSafetyService>();
        services.AddSingleton<IAuditSink, StructuredLogAuditSink>();

        // --- Skills and agents ---

        services.AddSingleton<SkillMetadataParser>();
        services.AddSingleton<ISkillMetadataRegistry, SkillMetadataRegistry>();
        services.AddSingleton<AgentMetadataParser>();
        services.AddSingleton<IAgentMetadataRegistry, AgentMetadataRegistry>();

        // --- Plugins ---

        services.AddSingleton<IPluginManifestReader, PluginManifestReader>();
        services.AddSingleton<IPluginRegistry, PluginRegistry>();

        // --- Tool execution ---

        services.AddSingleton<IToolConcurrencyClassifier, ToolConcurrencyClassifier>();
        services.AddTransient<IToolExecutionStrategy, BatchedToolExecutionStrategy>();

        // --- State management ---

        services.AddSingleton<IStateMarkdownGenerator, StateMarkdownGenerator>();
        services.AddSingleton<JsonCheckpointStateManager>();
        services.AddSingleton<CompositeStateManager>();

        // --- Hooks ---

        services.AddSingleton<IHookRegistry, InMemoryHookRegistry>();
        services.AddTransient<IHookExecutor, CompositeHookExecutor>();

        // --- System prompt composition ---

        services.AddSingleton<IPromptSectionCache, InMemoryPromptSectionCache>();
        services.AddSingleton<ISystemPromptComposer, MemoizedPromptComposer>();
        services.AddTransient<IPromptSectionProvider, AgentIdentitySectionProvider>();
        services.AddTransient<IPromptSectionProvider, ToolSchemasSectionProvider>();
        services.AddTransient<IPromptSectionProvider, PermissionRulesSectionProvider>();
        services.AddTransient<IPromptSectionProvider, SessionStateSectionProvider>();
        services.AddSingleton<IPromptCacheTracker, Sha256PromptCacheTracker>();

        // --- Context compaction ---

        services.AddSingleton<IAutoCompactStateMachine, AutoCompactStateMachine>();
        services.AddSingleton<IContextCompactionService, ContextCompactionService>();
        services.AddTransient<ICompactionStrategyExecutor, FullCompactionStrategy>();
        services.AddTransient<ICompactionStrategyExecutor, PartialCompactionStrategy>();
        services.AddTransient<ICompactionStrategyExecutor, MicroCompactionStrategy>();

        // --- Subagent orchestration ---

        services.AddSingleton<ISubagentToolResolver, SubagentToolResolver>();
        services.AddSingleton<IAgentMailbox, InMemoryAgentMailbox>();
        services.AddSingleton<ISubagentProfileRegistry, BuiltInSubagentProfiles>();

        // --- Delegation and supervision ---

        services.AddSingleton<IDelegationStore, JsonlDelegationStore>();
        services.AddKeyedSingleton<ISupervisorStrategy>("capability-match", (sp, _) =>
            new CapabilityMatchStrategy(sp.GetRequiredService<IOptionsMonitor<AppConfig>>()));
        services.AddSingleton<ISupervisor, CapabilityMatchSupervisor>();

        // --- Config discovery ---

        services.AddTransient<IConfigDiscoveryService, DirectoryWalkConfigDiscovery>();

        // --- Meta-harness services ---

        services.AddScoped<IHarnessProposer, OrchestratedHarnessProposer>();
        services.AddScoped<IEvaluationService, AgentEvaluationService>();
        services.AddScoped<IRegressionSuiteService, FileSystemRegressionSuiteService>();

        // --- Agent identity (credential providers, resolver, RBAC validator) ---

        RegisterIdentityServices(services);

        // --- ChangeProposal pipeline (orchestrator, audit, evidence, 4 gates,
        //     NotConfigured defaults). Inert until AppConfig.AI.Changes.Enabled. ---

        RegisterChangesServices(services);

        // --- Egress layer (per-skill allowlist policy + AntiSSRF terminal
        //     handler + JSONL audit + named HttpClient "egress"). Inert until
        //     AppConfig.AI.Egress.Enabled. ---

        RegisterEgressServices(services);

        // --- Incident-response plan registry (PR-5). Inert until a consumer
        //     declares plans under AppConfig.AI.IncidentResponse. ---

        RegisterIncidentResponseServices(services);

        // --- Content capture (PR-11). Per-attribute capture policy, default
        //     regex redaction filter, startup validator. Inert until the
        //     consumer flips AppConfig.AI.Telemetry.ContentCapture.Enabled. ---

        RegisterContentCaptureServices(services);

        // --- Magentic orchestration (PR-6). Span emitter + HITL bridge +
        //     change-proposal router. Inert until IMagenticOrchestrator.RunAsync
        //     is invoked. ---

        RegisterMagenticServices(services);

        // --- A2A surface (PR-7). Caller + callee dispatch, identity
        //     propagation, OTel span linking, and pluggable auth
        //     (in-process vs cross-process mTLS+JWT). Transport selected by
        //     AppConfig.AI.A2A.Surface.Transport. ---

        RegisterA2AServices(services);

        // --- Governance (permissions, escalation, resilience) ---

        RegisterGovernanceServices(services);
        RegisterEscalationServices(services);
        RegisterResilienceServices(services, appConfig);

        // --- Quality loop (drift detection, learnings) ---

        RegisterDriftDetectionServices(services);
        RegisterLearningsServices(services, appConfig);

        // --- Planner and sandbox ---

        RegisterPlannerDbContext(services, appConfig);
        RegisterPlannerServices(services);
        RegisterSandboxServices(services);

        services.AddOptions<Planner.PlannerOptions>()
            .Configure<IOptionsMonitor<AppConfig>>((opts, app) =>
            {
                var cfg = app.CurrentValue.AI.AgentFramework;
                if (!string.IsNullOrEmpty(cfg.DefaultDeployment))
                    opts.GenerationModel = cfg.DefaultDeployment;
                opts.ClientType = cfg.ClientType;
            });

        // --- Unified model routing ---

        services.AddSingleton(Options.Create(appConfig.AI.ModelRouting));
        services.AddSingleton(Options.Create(appConfig.AI.KnowledgeBridge));
        services.AddSingleton<ITaskComplexityHeuristic, TaskComplexityHeuristic>();
        services.AddSingleton<IEscalationTracker, EscalationTracker>();
        services.AddSingleton<ITaskComplexityClassifier, TaskComplexityClassifier>();
        services.AddSingleton<IModelRouter, ModelRouter>();

        // --- Tool output compression ---

        services.AddSingleton(Options.Create(appConfig.AI.ToolOutputCompression));
        services.AddTransient<ICompressionStrategy, JsonCompressionStrategy>();
        services.AddTransient<ICompressionStrategy, StructuredTextCompressionStrategy>();
        services.AddTransient<ICompressionStrategy, FreeTextCompressionStrategy>();
        services.AddTransient<IToolOutputCompressor, ToolOutputCompressor>();

        return services;
    }
}
