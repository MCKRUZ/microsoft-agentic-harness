using Domain.Common.Config.AI.A2A;
using Domain.Common.Config.AI.AIFoundry;
using Domain.Common.Config.AI.ContextManagement;
using Domain.Common.Config.AI.DriftDetection;
using Domain.Common.Config.AI.GitOps;
using Domain.Common.Config.AI.Hooks;
using Domain.Common.Config.AI.Identity;
using Domain.Common.Config.AI.IncidentResponse;
using Domain.Common.Config.AI.Learnings;
using Domain.Common.Config.AI.MCP;
using Domain.Common.Config.AI.Orchestration;
using Domain.Common.Config.AI.Permissions;
using Domain.Common.Config.AI.Planner;
using Domain.Common.Config.AI.Plugins;
using Domain.Common.Config.AI.RAG;
using Domain.Common.Config.AI.Resilience;
using Domain.Common.Config.AI.Routing;
using Domain.Common.Config.AI.Sandbox;

namespace Domain.Common.Config.AI;

/// <summary>
/// Configuration for AI services including MCP server/client settings,
/// agent framework, AI Foundry, and model selection.
/// </summary>
/// <remarks>
/// <para>
/// Configuration hierarchy:
/// <code>
/// AppConfig.AI
/// ├── AgentFramework    — Provider type and default deployment
/// ├── Embedding         — Optional dedicated embedding provider (falls back to AgentFramework)
/// ├── AIFoundry         — Azure AI Foundry persistent agents
/// ├── MCP               — Server-side MCP configuration (auth, tool assemblies)
/// ├── McpServers        — Client-side MCP server registry (external servers to connect to)
/// ├── A2A               — Agent-to-Agent protocol configuration
/// ├── ContextManagement — Compaction, tool result storage, and budget tracking
/// ├── Permissions       — Permission system for tool and file access approvals
/// ├── Hooks             — Lifecycle hook execution configuration
/// ├── Orchestration     — Subagent management and streaming execution
/// ├── Resilience        — LLM fallback chains, circuit breakers, retry, degraded mode
/// ├── Rag               — RAG pipeline: ingestion, retrieval, reranking, model tiering
/// ├── ModelRouting       — Unified model routing: complexity classification, tier selection, escalation
/// ├── KnowledgeBridge    — Conversation-to-Knowledge Bridge: fact extraction, confidence, timeout
/// ├── DriftDetection    — EWMA-based drift detection for quality regressions
/// ├── Learnings         — Cross-session learnings: feedback blending, decay, pruning
/// ├── Planner           — Plan execution: concurrency, timeouts, persistence
/// ├── Sandbox           — Sandbox execution: resource limits, isolation, containers
/// ├── ToolOutputCompression — Tool output compression: thresholds, LLM fallback, strategies
/// ├── Plugins              — Local plugin declarations for external skill/MCP discovery
/// ├── Egress               — Per-skill outbound egress allowlist + SSRF defense
/// └── IncidentResponse     — Named incident-response plans (skill sets, autonomy overrides, gate overlays)
/// </code>
/// </para>
/// </remarks>
public class AIConfig
{
    /// <summary>
    /// Gets or sets the agent framework provider and default deployment settings.
    /// </summary>
    public AgentFrameworkConfig AgentFramework { get; set; } = new();

    /// <summary>
    /// Gets or sets the optional dedicated embedding provider used by RAG and
    /// knowledge graph features. When unset, the harness reuses the
    /// <see cref="AgentFramework"/> client for embeddings if that provider
    /// supports them (AzureOpenAI, OpenAI). Required when
    /// <see cref="AgentFramework"/> selects a chat-only provider such as
    /// Anthropic or AzureAIInference.
    /// </summary>
    public EmbeddingConfig Embedding { get; set; } = new();

    /// <summary>
    /// Gets or sets the Azure AI Foundry configuration for persistent agents.
    /// </summary>
    public AIFoundryConfig AIFoundry { get; set; } = new();

    /// <summary>
    /// Gets or sets the MCP server-side configuration (when this app is the server).
    /// </summary>
    public McpConfig MCP { get; set; } = new();

    /// <summary>
    /// Gets or sets the MCP client-side server registry (external servers to consume).
    /// </summary>
    public McpServersConfig McpServers { get; set; } = new();

    /// <summary>
    /// Gets or sets the Agent-to-Agent protocol configuration.
    /// </summary>
    public A2AConfig A2A { get; set; } = new();

    /// <summary>
    /// Gets or sets the context management configuration for compaction, tool result
    /// storage, and budget tracking.
    /// </summary>
    public ContextManagementConfig ContextManagement { get; set; } = new();

    /// <summary>
    /// Gets or sets the permission system configuration controlling tool and file
    /// access approvals.
    /// </summary>
    public PermissionsConfig Permissions { get; set; } = new();

    /// <summary>
    /// Gets or sets the hook system configuration for lifecycle callback execution.
    /// </summary>
    public HooksConfig Hooks { get; set; } = new();

    /// <summary>
    /// Gets or sets the orchestration configuration for subagent management and
    /// streaming tool execution.
    /// </summary>
    public OrchestrationConfig Orchestration { get; set; } = new();

    /// <summary>
    /// Gets or sets the filesystem skill discovery configuration.
    /// Controls where SKILL.md files are loaded from.
    /// </summary>
    public SkillsConfig Skills { get; set; } = new();

    /// <summary>
    /// Gets or sets the filesystem agent manifest discovery configuration.
    /// Controls where AGENT.md files are loaded from.
    /// </summary>
    public AgentsConfig Agents { get; set; } = new();

    /// <summary>
    /// Gets or sets the RAG pipeline configuration for document ingestion,
    /// retrieval, reranking, query transformation, and model tiering.
    /// </summary>
    public RagConfig Rag { get; set; } = new();

    /// <summary>
    /// Unified model routing configuration for complexity-aware tier selection.
    /// Routes agent turns, RAG operations, and supervisor delegation to appropriate model tiers.
    /// </summary>
    public ModelRoutingConfig ModelRouting { get; set; } = new();

    /// <summary>
    /// Conversation-to-Knowledge Bridge configuration controlling automatic
    /// fact extraction from agent turns into the knowledge graph.
    /// </summary>
    public KnowledgeBridgeConfig KnowledgeBridge { get; set; } = new();

    /// <summary>
    /// LLM provider resilience configuration including fallback chains,
    /// circuit breakers, retry policies, and degraded mode behavior.
    /// </summary>
    public ResilienceConfig Resilience { get; set; } = new();

    /// <summary>
    /// EWMA-based drift detection configuration for identifying quality regressions.
    /// </summary>
    public DriftDetectionConfig DriftDetection { get; set; } = new();

    /// <summary>
    /// Cross-session learnings configuration controlling feedback blending,
    /// temporal decay, pruning schedules, and drift baseline adjustment.
    /// </summary>
    public LearningsConfig Learnings { get; set; } = new();

    /// <summary>Agent Governance Toolkit configuration.</summary>
    public GovernanceConfig Governance { get; init; } = new();

    /// <summary>
    /// Planner subsystem configuration: concurrency limits, timeouts,
    /// database path, and checkpoint behavior.
    /// </summary>
    public PlannerOptions Planner { get; set; } = new();

    /// <summary>
    /// Sandbox execution subsystem configuration: resource limits,
    /// isolation defaults, container settings, and per-tool overrides.
    /// </summary>
    public SandboxOptions Sandbox { get; set; } = new();

    /// <summary>
    /// Tool output compression configuration: thresholds, LLM fallback,
    /// and strategy selection for reducing context window consumption.
    /// </summary>
    public ToolOutputCompressionConfig ToolOutputCompression { get; set; } = new();

    /// <summary>
    /// Plugin system configuration: local plugin declarations for external skill
    /// and MCP server discovery from external directories.
    /// </summary>
    public PluginsConfig Plugins { get; set; } = new();

    /// <summary>
    /// Agent identity subsystem configuration (PR-1). Off by default — when
    /// enabled, <c>AgentFactory</c> resolves an <c>AgentIdentity</c> via the
    /// registered <c>IAgentIdentityResolver</c> at agent construction and stamps
    /// it onto <c>IAgentExecutionContext</c> for outbound RBAC checks.
    /// </summary>
    public AgentIdentityConfig Identity { get; set; } = new();

    /// <summary>
    /// Eval dashboard persistence configuration (Sub-phase 5.4). Off by default;
    /// host opts in to durable ingest by setting <see cref="EvalDashboardOptions.PersistenceEnabled"/>.
    /// </summary>
    public EvalDashboardOptions EvalDashboard { get; set; } = new();

    /// <summary>
    /// ChangeProposal pipeline configuration (PR-2). Off by default — when
    /// enabled the orchestrator defaults to Shadow mode so a misconfigured
    /// rollout never silently applies real changes.
    /// </summary>
    public ChangesConfig Changes { get; set; } = new();

    /// <summary>
    /// Per-skill outbound egress layer configuration (PR-3b). Off by default —
    /// when enabled the named <c>HttpClient</c> ("egress") composes the
    /// harness's allowlist <c>DelegatingHandler</c> above a
    /// <c>Microsoft.Security.AntiSSRF</c> handler so RFC 1918 / link-local /
    /// loopback / IMDS denies and connect-time DNS validation are enforced
    /// alongside the per-skill hostname allowlist.
    /// </summary>
    public EgressConfig Egress { get; set; } = new();

    /// <summary>
    /// Incident-response plan registry (PR-5). Empty by default — the resolver
    /// returns <c>null</c> for every incident type so the orchestrator
    /// behaves identically to a host with no incident concept at all.
    /// </summary>
    public IncidentResponsePlanConfig IncidentResponse { get; set; } = new();

    /// <summary>
    /// GitOps skill pack configuration (PR-9). Off by default — when enabled,
    /// the startup validator requires <c>ActiveController</c> to be set,
    /// a configured K8sGPT MCP server entry under <c>McpServers.Servers</c>,
    /// and the active controller's API base URL. No graceful degradation:
    /// misconfiguration fails-loud at boot rather than shipping a half-working
    /// skill at runtime.
    /// </summary>
    public GitOpsConfig GitOps { get; set; } = new();
}
