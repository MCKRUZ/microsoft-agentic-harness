using System.Reflection;
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

        // Deterministic spin / no-progress guard for the agent's live tool-call path (opt-in via
        // GovernanceConfig.ProgressGuard.Enabled). Consulted at the same chokepoint as the governor;
        // breaks the loop when the agent repeats an identical call or makes no progress. Scoped: one
        // per agent turn, reset between turns alongside the governor.
        services.AddScoped<Interfaces.Governance.IProgressEvaluator, Services.Governance.ProgressEvaluator>();

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

        // Context budget tracking
        services.AddSingleton<IContextBudgetTracker, ContextBudgetTracker>();

        // LLM usage capture — scoped so middleware and handler share the same instance per turn
        services.AddScoped<ILlmUsageCapture, Services.LlmUsageCapture>();

        // Per-turn token budget tracker — scoped so each request gets a fresh budget seeded
        // from AppConfig.AI.AgentFramework.DefaultTokenBudget. Consulted by TokenBudgetBehavior
        // for the pre-flight CanAfford check and the post-turn RecordUsage decrement.
        services.AddScoped<ITokenBudgetTracker, Services.AI.TokenBudgetTracker>();

        // Conversation-lifetime token budget tracker — singleton keyed internally by conversation id
        // so it outlives the per-turn scopes. Consulted between turns by the conversation loopers
        // (RunConversationCommandHandler, ConversationOrchestrator) to break gracefully when a
        // conversation exhausts its cumulative budget. Opt-in via ConversationTokenBudget (0 = off).
        services.AddSingleton<IConversationBudgetTracker, Services.AI.ConversationBudgetTracker>();

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
}
