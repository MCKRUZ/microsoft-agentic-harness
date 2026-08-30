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
using Application.AI.Common.Interfaces.Governance;
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
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Sandbox;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

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

        // Shared bounded-key-set-gated first-party ITool lookup — the one place ToolCapabilityResolver
        // (tool-composition capability model), ToolPermissionProfileResolver (sandbox capability
        // model), and ToolRiskClassifier (graded-autonomy risk) each resolve a tool's own declaration
        // from keyed DI, instead of independently-maintained copies of the same bounded-lookup safety
        // invariant. See FirstPartyToolLookup's remarks for why the key set must stay bounded.
        // IToolCatalog below needs the raw key list rather than the lookup abstraction, so it scans
        // `services` again on its own — a second one-time O(n) scan at startup, not a shared instance.
        services.AddSingleton(sp => new Services.Tools.FirstPartyToolLookup(
            sp, new HashSet<string>(KeyedToolRegistrationKeys(services), StringComparer.Ordinal)));

        // Sandbox capability enforcement — profile resolution and enforcement. The resolver reads a
        // tool's own ITool.RequiredCapabilities/MinimumIsolation declaration via the shared
        // FirstPartyToolLookup (#387).
        services.AddOptions<SandboxConfig>();
        services.AddSingleton(sp => new ToolPermissionProfileResolver(
            sp.GetRequiredService<Services.Tools.FirstPartyToolLookup>(),
            sp.GetRequiredService<IOptionsMonitor<SandboxConfig>>(),
            // Both optional (#419): a composition root that never calls AddGovernance still
            // constructs this widely-used singleton — it just gets no durable audit trail for an
            // ungoverned-dispatch refusal, and (absent an EnableAudit toggle to read) defaults to
            // the historically-correct "audit on" behavior. See the constructor's own remarks.
            sp.GetService<IGovernanceAuditService>(),
            sp.GetService<IOptionsMonitor<GovernanceConfig>>()));
        services.AddScoped<ICapabilityEnforcer, CapabilityEnforcer>();

        // Scoped agent execution context — carries agent identity through the pipeline
        services.AddScoped<IAgentExecutionContext, AgentExecutionContext>();

        // The composed tool-call admission chain — the trace recorder, the five gates, and the
        // pipeline that sequences them. Factored into its own registration method (below) so a test
        // fixture can call it too and build the same wiring the production container does, instead of
        // hand-rolling it.
        services.AddToolCallAdmissionChain();

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
        // graded-autonomy gate and escalation-severity derivation. Reads the shared
        // FirstPartyToolLookup registered above — see its remarks for why the key set must stay
        // bounded; this call site was missed by the original #387 sweep and probed keyed DI directly
        // (found during a later code-review pass on this same diff).
        services.AddSingleton<Interfaces.Tools.IToolRiskClassifier>(sp => new Services.Tools.ToolRiskClassifier(
            sp.GetRequiredService<Services.Tools.FirstPartyToolLookup>()));

        // Tool behaviour registry — what each tool declared it does, and who declared it. Singleton
        // because an external MCP server's declaration arrives on a discovery call and must still be
        // there for the tool call it governs, which happens later on a different scope. Registered
        // unconditionally: recording costs nothing, and only the posture in
        // GovernanceConfig.ToolBehaviorGating turns the recordings into a decision.
        services.AddSingleton<Interfaces.Tools.IToolBehaviorRegistry, Services.Tools.ToolBehaviorRegistry>();

        // Tool capability resolver — what a tool can do with the data that flows through it (source of
        // untrusted/sensitive content, or a costly sink), for the tool-composition check. A sibling to
        // the behaviour registry above, answering a different question. Registered unconditionally, like
        // it: resolving costs nothing, and only ToolCompositionGatingConfig's pairings turn a resolved
        // profile into a reported or enforced posture. Reads the shared FirstPartyToolLookup registered
        // above — see its remarks for why the key set must stay bounded.
        services.AddSingleton<Interfaces.Tools.IToolCapabilityResolver>(sp => new Services.Tools.ToolCapabilityResolver(
            sp.GetRequiredService<Services.Tools.FirstPartyToolLookup>(),
            sp.GetRequiredService<Interfaces.Tools.IToolBehaviorRegistry>(),
            sp.GetRequiredService<IOptionsMonitor<Domain.Common.Config.AI.GovernanceConfig>>()));

        // Tool composition analyzer + reporter — flags an agent's assembled tool set for an
        // untrusted-input/credential-reading tool co-resident with a file-write/code-exec/outbound-send
        // tool. Registered unconditionally and passively, like the capability resolver above; the
        // reporter is a concrete class rather than an interface because it has exactly one production
        // consumer (ToolChainBuilder) and exists only to keep that consumer's three call sites reporting
        // through the same channels identically — see its own remarks.
        services.AddSingleton<Interfaces.Governance.IToolCompositionAnalyzer, Services.Governance.ToolCompositionAnalyzer>();
        services.AddSingleton<Services.Governance.ToolCompositionReporter>();

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

        // Structured-output contract layer (#323) — schema-out, validated-parse-back, one repair
        // round-trip. Stateless; holds no per-request state.
        services.AddSingleton<Interfaces.AI.IStructuredOutputInvoker, StructuredOutput.StructuredOutputInvoker>();

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

        // Sanitize/redact/size-tier treatment for tool-call arguments and results persisted for
        // conversation replay — depends on ICompositeResponseSanitizer/IContentRedactionFilter,
        // registered by Infrastructure.AI.Governance's own DI setup; resolution is lazy, so
        // registration order between the two does not matter.
        services.AddSingleton<Interfaces.IToolCallReplayTreatment, Services.ToolCallReplayTreatment>();

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

        // OWASP Agentic Top-10 eval metrics. AddEvalMetric registers each non-keyed (so
        // EvalRunner's IEnumerable<IEvalMetric> lookup can actually find it) as well as keyed by
        // its own IEvalMetric.Key (#436 — these were previously keyed-only, invisible to
        // EvalRunner, and every case in eval-datasets/owasp-agentic-top-10.yaml silently scored
        // 0.0/Warn instead of ever running).
        services.AddEvalMetric<OwaspAsi01GoalHijackMetric>("owasp.asi01.goal_hijack");
        services.AddEvalMetric<OwaspAsi02ToolMisuseMetric>("owasp.asi02.tool_misuse");
        services.AddEvalMetric<OwaspAsi03PrivilegeAbuseMetric>("owasp.asi03.privilege_abuse");
        services.AddEvalMetric<OwaspAsi04SupplyChainMetric>("owasp.asi04.supply_chain");
        services.AddEvalMetric<OwaspAsi05CodeExecMetric>("owasp.asi05.code_exec");
        services.AddEvalMetric<OwaspAsi06MemoryPoisonMetric>("owasp.asi06.memory_poison");
        services.AddEvalMetric<OwaspAsi07InterAgentMetric>("owasp.asi07.inter_agent");
        services.AddEvalMetric<OwaspAsi08CascadingMetric>("owasp.asi08.cascading");
        services.AddEvalMetric<OwaspAsi09HumanTrustMetric>("owasp.asi09.human_trust");
        services.AddEvalMetric<OwaspAsi10RogueAgentMetric>("owasp.asi10.rogue_agent");

        // Governance-behaviour eval metric — grades the real per-invocation GovernanceTrace
        // (approval-bypass / observe-only / missing-escalation), independently of task outcome.
        services.AddEvalMetric<GovernanceBehaviorMetric>("governance.behavior");

        return services;
    }

    /// <summary>
    /// Registers the composed tool-call admission chain: the turn's governance trace recorder, the
    /// six gates it sequences, and the pipeline itself.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <remarks>
    /// <para>
    /// Registers the turn's governance trace recorder, the six admission gates
    /// (<see cref="Interfaces.Governance.IToolInvocationGovernor"/>,
    /// <see cref="Interfaces.Governance.IToolClassificationGate"/>,
    /// <see cref="Interfaces.Governance.IToolCallObserverChain"/>,
    /// <see cref="Interfaces.Governance.IAgentToolAuthorizationGate"/>,
    /// <see cref="Interfaces.Governance.IProgressEvaluator"/>,
    /// <see cref="Interfaces.Governance.ICallOnceGate"/>), and the
    /// <see cref="Interfaces.Governance.IToolCallAdmissionPipeline"/> that composes them, so both the
    /// production composition root and a test fixture that wants a real chain build it from the one
    /// place that knows the current wiring.
    /// </para>
    /// <para>
    /// <strong>TryAdd, not Add — and the registration order that depends on it.</strong> A caller that
    /// wants to control one gate (a mock governor, a classification gate that always redacts) must
    /// register its own implementation for that interface <em>before</em> calling this method. Calling
    /// this method first and registering the override after does not work: <c>TryAdd*</c> has already
    /// claimed the slot, so the override is silently ignored and the caller gets the production default
    /// instead — no compile error, no runtime error, just a fixture testing against the wrong collaborator.
    /// </para>
    /// <para>
    /// Deliberately excludes the collaborators the governor itself depends on that are not chain-specific
    /// — permission resolution, graded autonomy, the declarative policy engine, capability enforcement,
    /// denial tracking, audit — because those are the actual subject under test in most fixtures that
    /// build a real chain, not incidental wiring to get out of the way.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddToolCallAdmissionChain(this IServiceCollection services)
    {
        // The turn's governance audit trail every stage writes to. Scoped: one per agent turn, reset
        // by the chain between turns.
        services.TryAddScoped<Interfaces.Governance.IGovernanceTraceRecorder, Services.Governance.GovernanceTraceRecorder>();

        // Gate 2 — permission / graded-autonomy / capability / policy (opt-in via
        // GovernanceConfig.EnforceToolInvocation).
        services.TryAddScoped<Interfaces.Governance.IToolInvocationGovernor, Services.Governance.ToolInvocationGovernor>();

        // Human approval routing for the governor's "requires approval" verdict (opt-in via
        // GovernanceConfig.ToolApproval.Enabled, additionally gated on Escalation.Enabled).
        services.TryAddScoped<Interfaces.Governance.IToolApprovalRouter, Services.Governance.EscalationToolApprovalRouter>();

        // Gate 5 — the loop guard (opt-in via GovernanceConfig.ProgressGuard.Enabled).
        services.TryAddScoped<Interfaces.Governance.IProgressEvaluator, Services.Governance.ProgressEvaluator>();

        // Gate 3 — classification-aware DLP (opt-in via GovernanceConfig.DataClassification.Mode). The
        // file-system asset resolver ships here; consumers register more for their own tools.
        services.TryAddScoped<Interfaces.Governance.IToolClassificationGate, Services.Governance.DefaultToolClassificationGate>();
        services.TryAddSingleton<Interfaces.Governance.IAssetReferenceResolver, Services.Governance.FileSystemAssetReferenceResolver>();

        // Gate 4 — the host's own rules. No IToolCallObserver implementations ship by default;
        // registration of one is the opt-in. The chain itself is always registered so callers can
        // depend on it unconditionally.
        services.TryAddScoped<Interfaces.Governance.IToolCallObserverChain, Services.Governance.ToolCallObserverChain>();

        // Gate 1 — per-agent tool RBAC (opt-in via AI.Identity.ToolAuthorization.Enabled). Registered
        // unconditionally so an unregistered gate and a switched-off gate are never confusable.
        services.TryAddScoped<Interfaces.Governance.IAgentToolAuthorizationGate, Services.Governance.DefaultAgentToolAuthorizationGate>();

        // Gate 6 — durable call-once enforcement (opt-in via
        // GovernanceDurableStateConfig.CallOnceEnforcementEnabled; a tool may be declared call-once
        // and go unenforced with the toggle off, matching every other opt-in gate here). The policy
        // registry is a singleton — it must outlive the tool-resolution scope that populates it — and
        // is also injected into ToolChainBuilder. IToolCallLedger itself is registered by
        // Infrastructure.AI (RegisterGovernanceStateServices), not here: this layer never references
        // Infrastructure, and the ledger's two implementations live there.
        services.TryAddSingleton<Interfaces.Governance.IToolCallOncePolicy, Services.Governance.ToolCallOncePolicy>();
        services.TryAddScoped<Interfaces.Governance.ICallOnceGate, Services.Governance.CallOnceGate>();

        // The composed chain over the six gates above. Every execution path that can reach a tool
        // calls this and nothing else, so a gate added here reaches all of them at once.
        services.TryAddScoped<Interfaces.Governance.IToolCallAdmissionPipeline, Services.Governance.ToolCallAdmissionPipeline>();

        // Closes the approval loop (#325): reports what an approved action actually did, and
        // attributes a corrected retry to its failed predecessor. Singleton because the failure
        // memory must outlive any one conversation — that is the entire premise of the feature.
        // The reporter is scoped, matching the scoped pipeline it is injected into.
        services.TryAddSingleton<Interfaces.Escalation.IApprovalFailureMemory, Services.Escalation.InProcessApprovalFailureMemory>();
        services.TryAddScoped<Interfaces.Escalation.IApprovalExecutionReporter, Services.Escalation.DefaultApprovalExecutionReporter>();

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
