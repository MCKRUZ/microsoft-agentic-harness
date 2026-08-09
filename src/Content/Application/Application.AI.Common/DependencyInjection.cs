using System.Reflection;
using Microsoft.Extensions.Logging;
using Application.AI.Common.Evaluation.Interfaces;
using Application.AI.Common.Evaluation.Metrics.Governance;
using Application.AI.Common.Evaluation.Metrics.Owasp;
using Application.AI.Common.Extensions;
using Application.AI.Common.Factories;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Context;
using Application.AI.Common.Interfaces.Sandbox;
using Application.AI.Common.Interfaces.Skills;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.MediatRBehaviors;
using Application.AI.Common.OpenTelemetry;
using Application.AI.Common.Services.AI;
using Application.AI.Common.Services.Agent;
using Application.AI.Common.Services.Context;
using Application.AI.Common.Services.Sandbox;
using Application.AI.Common.Services.Skills;
using Application.AI.Common.Services.Tools;
using Application.Common.Interfaces.Telemetry;
using Domain.Common.Config.AI.Sandbox;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace Application.AI.Common;

/// <summary>
/// Dependency injection configuration for the Application.AI.Common layer.
/// Registers agent-specific MediatR pipeline behaviors that depend on agentic
/// abstractions (agent context, tool permissions, content safety, audit).
/// </summary>
/// <remarks>
/// <para>
/// Called from the Presentation composition root after <c>AddApplicationCommonDependencies</c>:
/// <code>
/// services.AddApplicationCommonDependencies(appConfig);
/// services.AddApplicationAIDependencies();
/// </code>
/// </para>
/// <para>
/// <strong>Agent Pipeline Behavior Order:</strong>
/// These behaviors wrap the generic behaviors registered by Application.Common.
/// The combined pipeline (outermost → innermost):
/// <list type="number">
///   <item><description><c>UnhandledExceptionBehavior</c> — safety net with agent context enrichment</description></item>
///   <item><description><c>AgentContextPropagationBehavior</c> — sets scoped agent identity</description></item>
///   <item><description><c>AuditTrailBehavior</c> — records IAuditable requests</description></item>
///   <item><description><c>ContentSafetyBehavior</c> — screens IContentScreenable requests</description></item>
///   <item><description><c>HookBehavior</c> — fires lifecycle hooks for tool and turn events</description></item>
///   <item><description><c>RetrievalAuditBehavior</c> — logs retrieval-augmented generation audit trails</description></item>
///   <item><description><c>ResponseSanitizationBehavior</c> — post-execution: sanitizes tool output for credentials, injection, exfiltration</description></item>
///   <item><description><c>ToolOutputCompressionBehavior</c> — post-execution: compresses large tool output for context window savings</description></item>
///   <item><description><c>KnowledgeExtractionBehavior</c> — post-turn: extracts facts to knowledge graph (fire-and-forget)</description></item>
///   <item><description><c>WorkEpisodeCaptureBehavior</c> — post-turn: records what the agent did as a WorkEpisode (fire-and-forget)</description></item>
/// </list>
/// </para>
/// </remarks>
public static class DependencyInjection
{
    /// <summary>
    /// Registers all Application.AI.Common dependencies into the service collection.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddApplicationAIDependencies(
        this IServiceCollection services)
    {
        var assembly = Assembly.GetExecutingAssembly();

        // Auto-discover MediatR handlers + FluentValidation validators defined in
        // Application.AI.Common (e.g. ReplayTraceWithPromptVersion, IngestEvalRun).
        // Application.Common scans its own assembly; this scan covers the AI-layer
        // CQRS surface so handlers actually wire into the pipeline at runtime.
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(assembly));
        services.AddValidatorsFromAssembly(assembly);

        // The one writer of a conversation's telemetry rollup, shared by every transport that runs a
        // turn (issue #280). Registered here, in the assembly that owns the type and both store
        // interfaces it needs, rather than beside the observability implementation: three consumers in
        // three projects depend on it, and a host that wired the CQRS layer without the observability
        // one would otherwise fail to construct its conversation handler at all.
        services.AddSingleton<IConversationTelemetryRecorder, ConversationTelemetryRecorder>();

        // Agent-specific pipeline behaviors — registered before Application.Common
        // behaviors so they wrap as the outermost layer
        services
            .AddTransient(typeof(IPipelineBehavior<,>), typeof(UnhandledExceptionBehavior<,>))
            // Establish the request scope ambient for the whole pipeline so singleton-cached agents'
            // context providers (e.g. memory recall) resolve the correct request-scoped services.
            .AddTransient(typeof(IPipelineBehavior<,>), typeof(AmbientRequestScopeBehavior<,>))
            .AddTransient(typeof(IPipelineBehavior<,>), typeof(AgentContextPropagationBehavior<,>))
            // Identity resolution runs after the propagation behavior so the agent id is set
            // before identity acquisition begins. No-op when AppConfig.AI.Identity.Enabled is false.
            .AddTransient(typeof(IPipelineBehavior<,>), typeof(AgentIdentityResolutionBehavior<,>))
            .AddTransient(typeof(IPipelineBehavior<,>), typeof(AuditTrailBehavior<,>))
            .AddTransient(typeof(IPipelineBehavior<,>), typeof(ContentSafetyBehavior<,>))
            // Tool permission + graded-autonomy risk + declarative policy now run on the agent's live
            // tool path via IToolInvocationGovernor (GovernedAIFunction), not as MediatR behaviors —
            // nothing in production implements IToolRequest, so the old ToolPermissionBehavior /
            // GovernancePolicyBehavior never fired for agent tool calls. They were removed to avoid
            // dead, drift-prone duplicates of the governor's logic.
            .AddTransient(typeof(IPipelineBehavior<,>), typeof(PromptInjectionBehavior<,>))
            // Pre-flight token budget gate: short-circuits IConsumesTokens requests whose
            // estimate exceeds the remaining scoped budget, then records actual usage post-turn.
            // Placed after request-screening behaviors and before the LLM-invoking handler.
            .AddTransient(typeof(IPipelineBehavior<,>), typeof(TokenBudgetBehavior<,>))
            .AddTransient(typeof(IPipelineBehavior<,>), typeof(HookBehavior<,>))
            .AddTransient(typeof(IPipelineBehavior<,>), typeof(RetrievalAuditBehavior<,>))
            .AddTransient(typeof(IPipelineBehavior<,>), typeof(ResponseSanitizationBehavior<,>))
            .AddTransient(typeof(IPipelineBehavior<,>), typeof(ToolOutputCompressionBehavior<,>))
            .AddTransient(typeof(IPipelineBehavior<,>), typeof(KnowledgeExtractionBehavior<,>))
            .AddTransient(typeof(IPipelineBehavior<,>), typeof(WorkEpisodeCaptureBehavior<,>))
            .AddTransient(typeof(IPipelineBehavior<,>), typeof(PromptUsageTrackingBehavior<,>));

        // Sandbox capability enforcement — profile resolution and enforcement
        services.AddOptions<SandboxConfig>();
        services.AddSingleton<ToolPermissionProfileResolver>();
        services.AddScoped<ICapabilityEnforcer, CapabilityEnforcer>();

        // Scoped agent execution context — carries agent identity through the pipeline
        services.AddScoped<IAgentExecutionContext, AgentExecutionContext>();

        // Per-invocation tool governor — runs the permission / graded-autonomy / capability / policy
        // checks on the agent's live tool-call path (opt-in via GovernanceConfig.EnforceToolInvocation)
        // and records the per-turn governance trace. Scoped: one per agent turn.
        services.AddScoped<Interfaces.Governance.IToolInvocationGovernor, Services.Governance.ToolInvocationGovernor>();

        // Human approval routing for the governor's "requires approval" verdict (opt-in via
        // GovernanceConfig.ToolApproval.Enabled, additionally gated on Escalation.Enabled). Without
        // this the verdict was recorded and the call blocked — nobody was ever asked. Scoped to match
        // the governor that consults it.
        services.AddScoped<Interfaces.Governance.IToolApprovalRouter, Services.Governance.EscalationToolApprovalRouter>();

        // Deterministic spin / no-progress guard for the agent's live tool-call path (opt-in via
        // GovernanceConfig.ProgressGuard.Enabled). Consulted at the same chokepoint as the governor;
        // breaks the loop when the agent repeats an identical call or makes no progress. Scoped: one
        // per agent turn, reset between turns alongside the governor.
        services.AddScoped<Interfaces.Governance.IProgressEvaluator, Services.Governance.ProgressEvaluator>();

        // Classification-aware DLP gate for the agent's live tool-call path (opt-in via
        // GovernanceConfig.DataClassification.Mode). Consulted at the same chokepoint as the governor;
        // resolves the asset a tool touches, classifies it via Purview, and blocks or redacts per policy.
        // Scoped: reads the per-turn agent identity for audit. The asset resolvers are the per-tool adapter
        // layer — the file-system reference resolver ships here; consumers register more for their tools.
        services.AddScoped<Interfaces.Governance.IToolClassificationGate, Services.Governance.DefaultToolClassificationGate>();
        services.AddSingleton<Interfaces.Governance.IAssetReferenceResolver, Services.Governance.FileSystemAssetReferenceResolver>();

        // Consumer-authored tool-call observers. The harness registers NO IToolCallObserver
        // implementations — registration is the opt-in, so the default composition resolves an empty
        // chain that the chokepoint skips outright. Consumers add their own domain rules ("never wire
        // over 10k") by registering IToolCallObserver in their host. The chain itself is always
        // registered so the turn handler can depend on it unconditionally. Scoped: reads the per-turn
        // agent identity and shares the approval router's lifetime.
        services.AddScoped<Interfaces.Governance.IToolCallObserverChain, Services.Governance.ToolCallObserverChain>();

        // Per-agent tool RBAC (opt-in via AI.Identity.ToolAuthorization.Enabled). Registered
        // unconditionally and reports its own off state by admitting, so that an unregistered gate and
        // a switched-off gate are never confusable at runtime. Scoped: it caches the workload identity
        // it resolves for the lifetime of one turn, plan step, or direct invocation.
        services.AddScoped<Interfaces.Governance.IAgentToolAuthorizationGate, Services.Governance.DefaultAgentToolAuthorizationGate>();

        // The composed admission chain over the five gates above. Every execution path that can reach
        // a tool — the agent turn, the Execution API, and the plan engine's tool, LLM and retrieval
        // steps — calls this and nothing else, so a gate added here reaches all five at once. Scoped:
        // it holds the five scoped gates and is reset once per turn.
        services.AddScoped<Interfaces.Governance.IToolCallAdmissionPipeline, Services.Governance.ToolCallAdmissionPipeline>();

        // AI telemetry configurator — registers AI SDK OTel sources and processors
        services.AddSingleton<ITelemetryConfigurator, AiTelemetryConfigurator>();

        // Tool chain builder — resolves and assembles tools via MCP + keyed DI
        services.AddSingleton<IToolChainBuilder, ToolChainBuilder>();

        // Skill prerequisite resolver — builds prerequisite maps from skills and tools
        services.AddSingleton<ISkillPrerequisiteResolver, SkillPrerequisiteResolver>();

        // Agent factories — context mapping and agent creation
        services.AddSingleton<AgentExecutionContextFactory>();
        services.AddSingleton<IAgentFactory, AgentFactory>();

        // Tool conversion — ITool to AITool bridge for keyed DI tools
        services.AddSingleton<IToolConverter, AIToolConverter>();

        // Tool risk classification — resolves a tool's declared blast radius for the
        // graded-autonomy gate and escalation-severity derivation.
        services.AddSingleton<Interfaces.Tools.IToolRiskClassifier, Services.Tools.ToolRiskClassifier>();

        // Tool catalog — enumerates the host's keyed ITool registrations for callers that need to
        // discover what they may invoke. Singleton because the registrations cannot change once the
        // container is built, and because resolving every tool per request would be pure waste.
        // Registered unconditionally and passively — nothing resolves it unless a host mounts a
        // surface that lists tools.
        //
        // The factory closes over `services` deliberately. Reading the keys at REGISTRATION time
        // would capture only the tools registered before this line, silently omitting the skill packs
        // and connector tools that register after it; reading them when the catalog is first resolved
        // sees the completed collection. This is what keeps discovery drift-free — there is no list
        // for a new tool's DI file to remember to update.
        services.AddSingleton<Interfaces.Tools.IToolCatalog>(sp => new Services.Tools.ToolCatalog(
            sp,
            KeyedToolRegistrationKeys(services),
            sp.GetRequiredService<ILogger<Services.Tools.ToolCatalog>>()));

        // Direct tool invocation — the single arming site for running a tool on behalf of an external
        // caller. Registered unconditionally and passively, like the catalog above: the feature gate is
        // read per request from AppConfig.AI.DirectToolInvocation (off by default in every host), so the
        // DI graph does not vary by configuration and ValidateOnBuild checks the same container
        // everywhere. Singleton because it holds no per-request state — it creates its own scope per
        // invocation for the scoped governor and execution context.
        services.AddSingleton<Interfaces.Tools.IDirectToolInvoker, Services.Tools.DirectToolInvoker>();

        // Context budget tracking
        services.AddSingleton<IContextBudgetTracker, ContextBudgetTracker>();

        // LLM usage capture — scoped so middleware and handler share the same instance per turn
        services.AddScoped<ILlmUsageCapture, Services.LlmUsageCapture>();

        // Per-turn token budget tracker — scoped so each request gets a fresh budget seeded
        // from AppConfig.AI.AgentFramework.DefaultTokenBudget. Consulted by TokenBudgetBehavior
        // for the pre-flight CanAfford check and the post-turn RecordUsage decrement.
        services.AddScoped<ITokenBudgetTracker, Services.AI.TokenBudgetTracker>();

        // IConversationBudgetTracker is deliberately NOT registered here, even though its in-process
        // implementation lives in this project. It is chosen by AppConfig.AI.Conversations.Provider
        // alongside the conversation store and the turn lease — all three must agree on how far a
        // conversation reaches — so Infrastructure.AI's RegisterConversationStore owns the choice.
        // Registering a default here as well would leave two registrations for one interface, with
        // which of them wins decided by the order the composition root happens to add the layers.

        // Per-conversation tracker of registrations (system prompt, skills, tools, MCP,
        // sub-agents) already emitted. Drives the per-turn context snapshot deltas so
        // the dashboard inspector shows what landed in context on each turn.
        services.AddSingleton<
            Interfaces.Context.IConversationRegistrationTracker,
            Services.Context.ConversationRegistrationTracker>();

        // Agent conversation cache — reuses the same AIAgent across all turns in a conversation
        services.AddMemoryCache();
        services.AddSingleton<IAgentConversationCache, Services.AgentConversationCache>();

        // Ambient bridge so singleton-cached agents' context providers can resolve the current
        // request's scoped services (e.g. tenant-aware IKnowledgeMemory) per invocation.
        services.AddSingleton<Interfaces.IAmbientRequestScope, Services.AmbientRequestScope>();

        // Skill completion tracking — conversation-scoped prerequisite state
        services.AddSingleton<ISkillCompletionTracker, InMemorySkillCompletionTracker>();

        // Skill-training subsystem (PatchApplier, GateEvaluator, schedulers, checkpoint store, ...)
        services.AddSkillTrainingDependencies();

        // Harmonic memory representation seams (Memora port) — fail-fast NotConfigured defaults, inert
        // until AppConfig:AI:HarmonicMemory:Mode is raised above Off.
        services.AddHarmonicMemoryDependencies();

        // OWASP Agentic Top-10 eval metrics — keyed by metric key for IEvalRunner resolution
        services
            .AddKeyedSingleton<IEvalMetric, OwaspAsi01GoalHijackMetric>("owasp.asi01.goal_hijack")
            .AddKeyedSingleton<IEvalMetric, OwaspAsi02ToolMisuseMetric>("owasp.asi02.tool_misuse")
            .AddKeyedSingleton<IEvalMetric, OwaspAsi03PrivilegeAbuseMetric>("owasp.asi03.privilege_abuse")
            .AddKeyedSingleton<IEvalMetric, OwaspAsi04SupplyChainMetric>("owasp.asi04.supply_chain")
            .AddKeyedSingleton<IEvalMetric, OwaspAsi05CodeExecMetric>("owasp.asi05.code_exec")
            .AddKeyedSingleton<IEvalMetric, OwaspAsi06MemoryPoisonMetric>("owasp.asi06.memory_poison")
            .AddKeyedSingleton<IEvalMetric, OwaspAsi07InterAgentMetric>("owasp.asi07.inter_agent")
            .AddKeyedSingleton<IEvalMetric, OwaspAsi08CascadingMetric>("owasp.asi08.cascading")
            .AddKeyedSingleton<IEvalMetric, OwaspAsi09HumanTrustMetric>("owasp.asi09.human_trust")
            .AddKeyedSingleton<IEvalMetric, OwaspAsi10RogueAgentMetric>("owasp.asi10.rogue_agent");

        // Governance-behaviour eval metric — grades the real per-invocation GovernanceTrace
        // (approval-bypass / observe-only / missing-escalation), independently of task outcome.
        // EvalRunner builds its metric map from the non-keyed IEnumerable<IEvalMetric> — and keyed
        // registrations are invisible to IEnumerable<T> — so this MUST be registered non-keyed to be
        // discoverable by MetricKey at run time. The keyed alias mirrors the canonical AddMetric pattern.
        services.AddSingleton<GovernanceBehaviorMetric>();
        services.AddSingleton<IEvalMetric>(sp => sp.GetRequiredService<GovernanceBehaviorMetric>());
        services.AddKeyedSingleton<IEvalMetric>(
            "governance.behavior", (sp, _) => sp.GetRequiredService<GovernanceBehaviorMetric>());

        return services;
    }

    /// <summary>
    /// The keys under which <see cref="ITool"/> implementations are registered in
    /// <paramref name="services"/>.
    /// </summary>
    /// <remarks>
    /// Materialized on each call so the caller observes the collection as it stands then; the tool
    /// catalog calls it when it is first resolved, by which point every skill pack and connector has
    /// registered. Non-string keys are skipped: tools are keyed by name throughout the harness, and a
    /// non-string key could not be supplied on the wire anyway.
    /// </remarks>
    private static IReadOnlyList<string> KeyedToolRegistrationKeys(IServiceCollection services) =>
    [
        .. services
            .Where(descriptor => descriptor.IsKeyedService && descriptor.ServiceType == typeof(ITool))
            .Select(descriptor => descriptor.ServiceKey as string)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key!)
    ];
}
