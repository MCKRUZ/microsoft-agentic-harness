using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Compression;
using Application.AI.Common.Interfaces.Agents;
using Application.AI.Common.Interfaces.Compaction;
using Application.AI.Common.Interfaces.Config;
using Application.AI.Common.Interfaces.Hooks;
using Application.AI.Common.Interfaces.MetaHarness;
using Application.AI.Common.Interfaces.Prompts;
using Application.AI.Common.Interfaces.Skills;
using Application.AI.Common.Interfaces.Memory;
using Application.AI.Common.Interfaces.Routing;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Interfaces.Traces;
using Application.Common.Factories;
using Domain.Common.Config;
using Infrastructure.AI.Agents;
using Infrastructure.AI.Audit;
using Infrastructure.AI.Compaction;
using Infrastructure.AI.Compaction.Strategies;
using Infrastructure.AI.Compression;
using Infrastructure.AI.Compression.Strategies;
using Infrastructure.AI.Config;
using Infrastructure.AI.ContentSafety;
using Infrastructure.AI.Factories;
using Infrastructure.AI.Generators;
using Infrastructure.AI.Hooks;
using Infrastructure.AI.Memory;
using Infrastructure.AI.MetaHarness;
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

        // Default filesystem implementation for normal agent runs.
        // CandidateSkillContentProvider is NOT registered here; the evaluator constructs it
        // directly with a HarnessCandidate snapshot for candidate-isolated evaluation.
        services.AddTransient<ISkillContentProvider, FileSystemSkillContentProvider>();

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

        // --- Agent history ---

        services.AddSingleton<Func<ITraceWriter, IAgentHistoryStore>>(
            _ => tw => new JsonlAgentHistoryStore(tw));

        // --- Meta-harness services ---

        services.AddScoped<IHarnessProposer, OrchestratedHarnessProposer>();
        services.AddScoped<IEvaluationService, AgentEvaluationService>();
        services.AddScoped<IRegressionSuiteService, FileSystemRegressionSuiteService>();

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
