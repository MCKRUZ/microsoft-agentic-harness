# Application.AI.Common

## What This Is

Application.AI.Common is the brain of the agentic harness. While Application.Common handles generic request plumbing (validation, caching, timeouts), this project handles everything that makes the system an *agent* platform: tool permission enforcement, content safety screening, context budget tracking, prompt composition, hook lifecycle management, governance policy evaluation, and the factories that wire agents together from their manifests. Think of it as the control tower that decides what an agent is allowed to do, how much context it has left, and whether its inputs/outputs are safe.

It solves the problem of scattered agent infrastructure: without a centralized Application layer for AI concerns, every handler would re-implement permission checks, every tool call would skip safety screening, and context budgets would be untracked until the LLM returned a "context too long" error.

This project depends on Application.Common (pipeline behaviors, logging), Domain.AI (agent/skill/tool domain models), and Domain.Common (Result pattern, config). It is depended upon by Application.Core (CQRS handlers), Infrastructure.AI (implementations), and Presentation layers.

## Architecture Context

```
                    ┌─────────────────────────┐
                    │     Presentation         │  (calls AddApplicationAIDependencies)
                    └────────────┬────────────┘
                                 │
                    ┌────────────▼────────────┐
                    │     Infrastructure.AI    │  (implements the 58+ interfaces defined here)
                    └────────────┬────────────┘
                                 │
        ┌────────────────────────┼──────────────────────────┐
        │                        │                          │
┌───────▼────────┐   ┌──────────▼──────────┐   ╔══════════▼══════════╗
│ Application.Core│   │ Application.Common   │   ║ Application.AI.Common║
│ (uses factories │   │ (generic pipeline)   │   ║ ← YOU ARE HERE      ║
│  and contexts)  │   └──────────────────────┘   ╚══════════╤══════════╝
└────────┬────────┘                                         │
         └──────────────────────────────────────────────────┤
                    ┌────────────────────────────────────────┤
                    │               ┌────────────────────────┤
            ┌───────▼───────┐  ┌───▼───────────┐  ┌────────▼────────┐
            │  Domain.AI     │  │ Domain.Common  │  │ App.Common      │
            └───────────────┘  └───────────────┘  └─────────────────┘
```

This is an Application layer project, so it defines interfaces (contracts) and orchestration logic but never touches external services directly. It cannot reference HTTP clients, database connections, or AI SDK implementations -- only their abstractions. The 58+ interfaces it defines are implemented by Infrastructure projects.

## Key Concepts

### The Agent-Specific Pipeline

When a request marked as agent-related flows through MediatR, it passes through AI-specific behaviors layered *on top of* Application.Common's generic pipeline:

```
Request enters
  → UnhandledExceptionBehavior     Outer safety net — catches, logs, enriches with agent context
    → AgentContextPropagation      Sets scoped agent identity (AgentId, ConversationId, TurnNumber)
      → AuditTrailBehavior         Records who did what, when, and the outcome
        → ContentSafetyBehavior    Screens IContentScreenable against safety policies
          → PromptInjectionBehavior Scans for injection attacks
            → HookBehavior     Fires pre/post lifecycle hooks
                  → RetrievalAuditBehavior  Records RAG retrieval metrics
                    → ToolOutputCompressionBehavior  Compresses large tool outputs by content type
                      → [Generic behaviors from Application.Common]
                      → Handler
```

> **Tool-call authorization** (permission ACLs, graded-autonomy risk, declarative policy) does **not**
> run as a MediatR behavior. The agent's autonomous tool calls never produce a MediatR request, so it
> is enforced on the live tool path by `IToolInvocationGovernor` (via `GovernedAIFunction`). The earlier
> `ToolPermissionBehavior` / `GovernancePolicyBehavior` were removed because nothing implements
> `IToolRequest` in production, so they never fired.

Requests opt into specific behaviors via marker interfaces:

```csharp
// This command participates in content safety, agent context, and observability:
public record ExecuteAgentTurnCommand :
    IRequest<AgentTurnResult>,
    IAgentScopedRequest,       // → AgentContextPropagation activates
    IContentScreenable,        // → ContentSafetyBehavior screens input
    IHasObservabilitySession   // → Records metrics to observability store
{
    public string ContentToScreen => UserMessage;
    public ContentScreeningTarget ScreeningTarget => ContentScreeningTarget.Input;
    public string AgentId => AgentName;
    // ...
}
```

### Agent Factories

Two factories handle the complex construction of agent instances:

**AgentFactory** creates fully-configured `AIAgent` instances from the Microsoft.Agents.AI framework. It:
1. Validates the requested AI provider is configured
2. Creates a chat client via `IChatClientFactory`
3. Builds the middleware pipeline (OpenTelemetry, function invocation, observability, tool diagnostics, distributed cache)
4. Wires tools and context providers
5. Optionally wires `SkillPrerequisiteMiddleware` for multi-skill prerequisite enforcement
6. Returns a ready-to-use agent

```csharp
// Creating an agent from a skill:
var agent = await agentFactory.CreateAgentFromSkillAsync("agents/research");

// Multi-skill agent with merged instructions, tools, and prerequisite ordering:
var agent = await agentFactory.CreateAgentFromSkillsAsync(
    new[] { "agents/research", "agents/analysis" });

// Batch creation by category:
var analysts = await agentFactory.CreateAgentsByCategoryAsync("analysis");

// Provisioning a persistent agent on Azure AI Foundry:
var (agent, agentId) = await agentFactory.CreatePersistentAgentAsync(context);
```

**AgentExecutionContextFactory** maps a `SkillDefinition` to a runtime `AgentExecutionContext`. It resolves tools (MCP-first, then keyed DI fallback), assembles instructions, configures middleware, and sets deployment parameters. For multi-skill agents it also:
- Merges instructions and tool sets across skills
- Supports dual skill mode (Managed uses ToolDeclarations, Injected gets all MCP tools)
- Applies plugin-boundary governance filtering (AllowedTools/DeniedTools from PluginDeclaration)
- Computes the `SkillPrerequisiteMap` for prerequisite middleware
- Enforces required tool resolution (throws for unresolvable required tools)

### Context Budget Tracking

The `ContextBudgetTracker` is the agent's accountant. It tracks token allocation across four categories: system prompt, loaded skills, tool schemas, and conversation history. When utilization hits warning thresholds, it signals the need for compaction.

```csharp
// Checking budget before loading a skill:
var budget = contextBudgetTracker.GetBudget(agentId);
if (budget.RemainingTokens < skill.Level2TokenEstimate)
    // Fall back to Tier 1 only, or trigger compaction
```

The `TieredContextAssembler` works alongside it, deciding which skill tier to load based on remaining budget: Tier 1 always, Tier 2 when budget allows, Tier 3 only during active execution.

### Tool Conversion (ITool to AITool)

The harness has its own `ITool` interface -- richer than the framework's `AITool`, with named operations, concurrency classification, and structured execution results. The `AIToolConverter` bridges the gap:

```csharp
// ITool (harness abstraction):
public interface ITool
{
    string Name { get; }
    string Description { get; }
    IReadOnlyList<string> SupportedOperations { get; }
    bool IsReadOnly => false;
    Task<ToolResult> ExecuteAsync(string operation, IReadOnlyDictionary<string, object?> parameters, CancellationToken ct);
}

// AIToolConverter transforms this into an AIFunction the LLM sees:
var aiTools = converter.ConvertTools(resolvedTools);
// These go into ChatOptions.Tools for the chat completion call
```

### Content Safety Screening

The `ContentSafetyBehavior` screens any request implementing `IContentScreenable`. If the safety service blocks the content, the handler never executes:

```csharp
// Input is screened before reaching the handler:
var result = await _safetyService.ScreenAsync(screenable.ContentToScreen, ct);
if (result.IsBlocked)
    return Result<T>.ContentBlocked(result.BlockReason);
```

Output screening happens at the agent orchestration loop level (after the LLM responds), since the pipeline behavior only has access to the typed *request*, not the generic response.

### The 58+ Interfaces

The interface surface defines the contracts that Infrastructure must implement:

| Category | Interfaces | Purpose |
|----------|-----------|---------|
| **Agent** | `IAgentFactory`, `IChatClientFactory`, `IAgentExecutionContext` | Agent creation and LLM client management |
| **Agents** | `IAgentMailbox`, `ISubagentProfileRegistry`, `ISubagentToolResolver` | Inter-agent messaging, subagent orchestration |
| **Attestation** | `IAttestationService`, `IAttestationStore` | HMAC-signed execution attestations and audit persistence |
| **Compaction** | `IContextCompactionService`, `IAutoCompactStateMachine`, `ICompactionStrategyExecutor` | Context window reduction when budget exhausted |
| **Compression** | `ICompressionStrategy`, `IToolOutputCompressor` | Tool output compression with strategy dispatch |
| **Config** | `IConfigDiscoveryService` | Filesystem config file discovery |
| **Connectors** | `IConnectorClient`, `IConnectorClientFactory`, `ConnectorToolAdapter` | External service integrations as tools |
| **Context** | `IContextBudgetTracker`, `ITieredContextAssembler`, `IToolResultStore` | Token budget and progressive skill loading |
| **Governance** | `IGovernancePolicyEngine`, `IGovernanceAuditService`, `IMcpSecurityScanner`, `IPromptInjectionScanner` | Policy enforcement and threat detection |
| **Hooks** | `IHookExecutor`, `IHookRegistry` | Lifecycle event interception |
| **KnowledgeGraph** | `IKnowledgeGraphStore`, `IKnowledgeMemory`, `IFeedbackStore`, `IProvenanceStamper` | Graph-backed cross-session memory |
| **Memory** | `IAgentHistoryStore` | Agent decision/audit log (tool-call/result/decision events) |
| **MetaHarness** | `IEvaluationService`, `IHarnessProposer`, `ISnapshotBuilder` | Automated harness optimization |
| **Permissions** | `IDenialTracker`, `IPatternMatcher`, `IPermissionRuleProvider`, `ISafetyGateRegistry` | Tool access control |
| **Planner** | `IPlanExecutor`, `IPlanValidator`, `IPlanStateStore`, `IPlanProgressNotifier`, `IPlanStepExecutor`, `IPlanGenerator` | DAG-based plan execution, validation, persistence, and generation |
| **Plugins** | `IPluginLoader`, `IPluginManifestReader`, `IPluginRegistry` | Local plugin loading, manifest reading, and tracking |
| **Prompts** | `ISystemPromptComposer`, `IPromptSectionProvider`, `IPromptSectionCache`, `IPromptCacheTracker` | System prompt assembly and caching |
| **RAG** | `IRagOrchestrator`, `IHybridRetriever`, `IVectorStore`, `IReranker`, `ICragEvaluator`, etc. | Full RAG pipeline (14 interfaces) |
| **Safety** | `ITextContentSafetyService` | Content screening |
| **Sandbox** | `ISandboxExecutor`, `ICapabilityEnforcer`, `IProcessResourceLimiter` | Sandboxed tool execution with capability-based permissions |
| **Skills** | `ISkillPrerequisiteResolver`, `ISkillCompletionTracker` | Prerequisite ordering and conversation-scoped completion tracking (SKILL.md content loading is owned by MAF's `AgentSkillsProvider`) |
| **Tools** | `ITool`, `IToolConverter`, `IToolExecutionStrategy`, `IToolConcurrencyClassifier`, `IFileSystemService` | Tool abstraction and execution |
| **Traces** | `IExecutionTraceStore`, `ITraceWriter` | Execution trace persistence |

### OpenTelemetry Metrics

Eleven metric instruments track agent operations across the system. All use the conventions from Domain.AI:

- `ContentSafetyMetrics` -- Evaluations (counter), Blocks (counter by category)
- `ContextBudgetMetrics` -- Utilization (gauge), Compaction triggers (counter)
- `LlmUsageMetrics` -- Token consumption (histogram), cost (histogram by model)
- `OrchestrationMetrics` -- Conversation duration, turns per conversation, tool calls
- `SessionMetrics` -- Active sessions (up/down counter), session cost
- `ToolExecutionMetrics` -- Execution duration (histogram), success/failure rate

## Project Structure

```
Application.AI.Common/
├── DependencyInjection.cs             # Registers 12 behaviors, factories, services
├── Exceptions/                        # 8 agent-specific exceptions
│   ├── AgentExecutionException.cs     # Agent turn failure
│   ├── AttackDetectionException.cs    # Prompt injection detected
│   ├── ContentSafetyException.cs      # Content blocked
│   ├── ContextBudgetExceededException.cs  # Token limit hit
│   ├── McpConnectionException.cs      # MCP server unreachable
│   ├── SkillNotFoundException.cs      # Skill ID not in registry
│   ├── SkillParsingException.cs       # Malformed SKILL.md
│   └── ToolExecutionException.cs      # Tool returned error
├── Extensions/                        # AgentContext helpers, structured logging
├── Factories/
│   ├── AgentFactory.cs                # Creates AIAgent with full middleware pipeline
│   └── AgentExecutionContextFactory.cs # Maps SkillDefinition → runtime context
├── Helpers/
│   ├── PromptTemplateHelper.cs        # Mustache-style {{variable}} substitution
│   └── TokenEstimationHelper.cs       # Approximate token counting
├── Interfaces/                        # 160+ contracts across 25+ subdirectories
│   ├── Attestation/
│   │   ├── IAttestationService.cs     # HMAC sign/verify tool execution attestations
│   │   └── IAttestationStore.cs       # Persist/retrieve attestations for audit trail
│   ├── Planner/
│   │   ├── IPlanExecutor.cs           # ExecuteAsync, CancelAsync, RetryStepAsync
│   │   ├── IPlanValidator.cs          # DAG validation (cycles, reachability, edges)
│   │   ├── IPlanStateStore.cs         # SavePlan, LoadPlan, UpdateStepState, Checkpoint, Resume
│   │   ├── IPlanProgressNotifier.cs   # Real-time step/plan progress notifications
│   │   ├── IPlanStepExecutor.cs       # Step-type-specific execution (keyed by StepType)
│   │   └── IPlanGenerator.cs          # LLM-based plan generation from natural language
│   ├── Sandbox/
│   │   ├── ISandboxExecutor.cs        # Isolated tool execution (keyed by SandboxIsolationLevel)
│   │   ├── ICapabilityEnforcer.cs     # Three-phase capability permission resolution
│   │   └── IProcessResourceLimiter.cs # OS-level process resource limits (Job Objects)
│   ├── Compression/
│   │   ├── ICompressionStrategy.cs    # Strategy for content-type-specific compression
│   │   └── IToolOutputCompressor.cs   # Orchestrates detection + strategy dispatch
│   ├── Plugins/
│   │   ├── IPluginLoader.cs           # Load plugins from local directories
│   │   ├── IPluginManifestReader.cs   # Read plugin.json manifests
│   │   └── IPluginRegistry.cs         # Track loaded plugins
│   ├── Skills/
│   │   └── ISkillCompletionTracker.cs # Conversation-scoped prerequisite tracking
│   ├── SkillTraining/
│   │   ├── IGateEvaluator.cs          # Pure gate decision (Hard/Soft/Mixed, strict-greater)
│   │   ├── IPatchProposer.cs          # The "optimizer" role — reflects on rollouts, emits a Patch
│   │   ├── IPatchAggregator.cs        # Dedup equivalent edits, sum SupportCount across patches
│   │   ├── IEditSelector.cs           # Rank by support + merge level, top-K clip (LR budget)
│   │   ├── ILrScheduler.cs            # (step, totalSteps, lrStart, lrMin) → int edits/step
│   │   ├── IRolloutRunner.cs          # Run candidate skill against an item batch
│   │   └── ISkillTrainingCheckpointStore.cs   # Save/Get/GetBest/List checkpoints per run
│   ├── ... (19 other subdirectories)
├── MediatRBehaviors/
│   ├── AgentContextPropagationBehavior.cs  # Sets scoped agent identity
│   ├── AuditTrailBehavior.cs          # Records who/what/when/outcome
│   ├── ContentSafetyBehavior.cs       # Input screening
│   ├── HookBehavior.cs               # Pre/post lifecycle hooks
│   ├── PromptInjectionBehavior.cs     # Injection attack detection
│   ├── ResponseSanitizationBehavior.cs # Sanitizes response content
│   ├── RetrievalAuditBehavior.cs      # RAG retrieval metrics
│   ├── TokenBudgetBehavior.cs         # Token budget enforcement
│   ├── ToolOutputCompressionBehavior.cs # Compresses large tool outputs by content type
│   └── UnhandledExceptionBehavior.cs  # Outer safety net
├── Middleware/
│   ├── ObservabilityMiddleware.cs     # Chat client middleware: tracks LLM calls
│   ├── SkillPrerequisiteMiddleware.cs # Enforces skill prerequisite ordering per-turn
│   └── ToolDiagnosticsMiddleware.cs   # Chat client middleware: logs tool usage
├── Models/
│   ├── Context/ContextModels.cs       # Token allocation, budget status
│   ├── SkillPrerequisiteMap.cs        # Prerequisite graph with cycle detection
│   └── Tools/                         # ToolExecutionProgress, Request, Result
├── OpenTelemetry/
│   ├── AiTelemetryConfigurator.cs     # Registers AI SDK OTel sources
│   ├── Instruments/AiSourceNames.cs   # Source name constants
│   ├── Metrics/                       # 11 metric instrument classes
│   └── Processors/                    # AgentFramework + Conversation span processors
└── Services/
    ├── Agent/
    │   ├── AgentExecutionContext.cs    # IAgentExecutionContext implementation (scoped)
    │   └── ToolPermissionFilter.cs    # Filters tools by permission rules
    ├── Context/
    │   ├── ContextBudgetTracker.cs    # Token budget management
    │   └── TieredContextAssembler.cs  # Progressive skill tier loading
    ├── LlmUsageCapture.cs             # Per-turn token/cost accumulator
    ├── Skills/
    │   └── InMemorySkillCompletionTracker.cs  # In-memory prerequisite completion tracking
    ├── SkillTraining/                 # SkillOpt-port skill-document training loop (pure components)
    │   ├── PatchApplier.cs             # Apply ordered edits to markdown; per-edit success/failure report
    │   ├── GateEvaluator.cs            # Pure gate (Hard/Soft/Mixed projection, finite-checked)
    │   ├── PatchAggregator.cs          # Dedup edits by (Op, Target, Content), sum SupportCount
    │   ├── TopKEditSelector.cs         # Stable ranking by support → merge level → insertion order
    │   ├── RolloutBatchScorer.cs       # Project per-item rollouts to batch (hard, soft) means
    │   ├── InMemorySkillTrainingCheckpointStore.cs  # Best + latest 64 per run; concurrent-safe
    │   ├── NotConfiguredPatchProposer.cs    # Fail-fast default — replace before TrainSkillCommand
    │   ├── NotConfiguredRolloutRunner.cs    # Fail-fast default — replace before TrainSkillCommand
    │   └── Schedulers/                # CosineScheduler, LinearScheduler, ConstantScheduler, SchedulerArgs
    └── Tools/
        ├── AIToolConverter.cs         # ITool → AIFunction bridge
        └── ToolDescriptionBuilder.cs  # Generates LLM-friendly tool descriptions
```

### Skill Training CQRS

The `CQRS/SkillTraining/` subtree wires the SkillOpt-port training loop into the standard MediatR pipeline:

| Command | Purpose |
|---|---|
| `TrainSkillCommand` | Main orchestrator. Chains rollout → reflect → aggregate → select → apply → gate for N epochs × M steps with patience-based early stop. |
| `GateCandidateSkillCommand` | Pure gate decision over pre-computed (hard, soft) scores; returns `GateResult{Action, ...}`. |
| `ReflectOnFailuresCommand` | Delegates to `IPatchProposer`; converts exceptions to stable scrubbed `Result.Fail` codes (no exception text leaks). |
| `SlowUpdateCommand` | Paired longitudinal classification (improved/regressed/persistent_fail/stable_success) + guidance synthesis at epoch boundary. |
| `MetaSkillUpdateCommand` | Persists cross-epoch strategy memory via `IKnowledgeMemory` under `skill-training/meta/{skillId}/{runId}`. |

All validators enforce score ranges in [0, 1], `MaxSkillLength = 262_144` on every skill string, and cross-field invariants (`BestStep ≤ GlobalStep`, `IncludeSuccesses=false` requires ≥1 failure rollout). DI is auto-wired via `AddSkillTrainingDependencies()` from `Application.AI.Common/Extensions/`; consumers replace the `NotConfigured*` defaults with agent-backed Infrastructure impls.

## Key Types Reference

| Type | Purpose | Used By |
|------|---------|---------|
| **Factories** | | |
| `AgentFactory` | Creates AIAgent with middleware pipeline | ExecuteAgentTurnHandler |
| `AgentExecutionContextFactory` | SkillDefinition → AgentExecutionContext | AgentFactory |
| **Behaviors** | | |
| `ContentSafetyBehavior<,>` | Input screening via ITextContentSafetyService | IContentScreenable requests |
| `HookBehavior<,>` | Pre/post lifecycle hook dispatch | All requests (checks hook registry) |
| `IToolInvocationGovernor` | Tool permission + graded-autonomy risk + policy on the live tool path | GovernedAIFunction (agent tool calls) |
| **Services** | | |
| `ContextBudgetTracker` | Tracks token allocation per agent | TieredContextAssembler, compaction |
| `TieredContextAssembler` | Loads skill tiers based on budget | AgentExecutionContextFactory |
| `AIToolConverter` | Converts ITool → AIFunction | AgentFactory tool wiring |
| **Interfaces** | | |
| `IRagOrchestrator` | Full RAG pipeline entry point | DocumentSearchTool, SearchDocumentsHandler |
| `ITool` | Framework-independent tool contract | All tool implementations |
| `IGovernancePolicyEngine` | Policy evaluation | IToolInvocationGovernor |
| **Planner** | | |
| `IPlanExecutor` | DAG plan execution with checkpoint/resume | ExecutePlanCommandHandler |
| `IPlanValidator` | Structural + semantic plan validation | PlanExecutor, CreatePlanCommandHandler |
| `IPlanStateStore` | Plan persistence with optimistic concurrency | PlanExecutor, all Planner CQRS handlers |
| `IPlanProgressNotifier` | Real-time plan/step progress events | PlanExecutor, Presentation layer |
| `IPlanStepExecutor` | Step-type-specific execution (keyed DI) | PlanExecutor.Scheduling |
| `IPlanGenerator` | LLM-based plan generation | GeneratePlanCommandHandler |
| **Sandbox** | | |
| `ISandboxExecutor` | Isolated tool execution (keyed DI) | ToolUseStepExecutor |
| `ICapabilityEnforcer` | Capability-based permission enforcement | ToolUseStepExecutor, sandbox executors |
| `IProcessResourceLimiter` | OS-level process resource limits | ProcessSandboxExecutor |
| **Attestation** | | |
| `IAttestationService` | HMAC sign/verify execution results | Sandbox executors, ToolUseStepExecutor |
| `IAttestationStore` | Attestation audit trail persistence | PlanExecutor, audit queries |
| **Compression** | | |
| `ToolOutputCompressionBehavior<,>` | Compresses large tool outputs by content type | All agent-scoped requests |
| `ICompressionStrategy` | Content-type-specific compression strategy | ToolOutputCompressor |
| `IToolOutputCompressor` | Orchestrates content detection + strategy dispatch | ToolOutputCompressionBehavior |
| **Plugins** | | |
| `IPluginRegistry` | Track and lookup loaded plugins | AgentExecutionContextFactory |
| `IPluginLoader` | Load plugins from local directories | Startup, plugin discovery |
| **Skills (Prerequisite)** | | |
| `SkillPrerequisiteMiddleware` | Enforces skill prerequisite ordering per-turn | DelegatingChatClient pipeline |
| `ISkillCompletionTracker` | Conversation-scoped prerequisite tracking | SkillPrerequisiteMiddleware |
| `SkillPrerequisiteMap` | Prerequisite graph with topological cycle detection | AgentFactory, middleware |

## Common Tasks

### How to Add a New Pipeline Behavior (Agent-Specific)

1. Create the behavior in `MediatRBehaviors/`:

```csharp
public sealed class MyAgentBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        if (request is not IMyAgentMarker agentRequest)
            return await next();  // Only activate for marked requests

        // Your pre-handler logic (e.g., check something)
        var response = await next();
        // Your post-handler logic (e.g., record something)
        return response;
    }
}
```

2. Register in `DependencyInjection.cs` (position relative to other behaviors matters):

```csharp
services.AddTransient(typeof(IPipelineBehavior<,>), typeof(MyAgentBehavior<,>));
```

### How to Define a New Interface for Infrastructure to Implement

1. Create in the appropriate `Interfaces/` subdirectory:

```csharp
namespace Application.AI.Common.Interfaces.MyFeature;

public interface IMyFeatureService
{
    Task<Result<MyOutput>> ProcessAsync(MyInput input, CancellationToken ct = default);
}
```

2. The implementation goes in `Infrastructure.AI/` or `Infrastructure.Common/`
3. Registration goes in the Infrastructure project's `DependencyInjection.cs`

### How to Add a New Metric

1. Create a static class in `OpenTelemetry/Metrics/`:

```csharp
public static class MyFeatureMetrics
{
    private static readonly Meter Meter = new("AgenticHarness.MyFeature", "1.0.0");

    public static readonly Counter<long> Operations = Meter.CreateCounter<long>(
        "harness.my_feature.operations",
        description: "Number of my feature operations");
}
```

2. Register the meter source in `AiTelemetryConfigurator.cs`

## Dependencies

| Reference | Why |
|-----------|-----|
| `Application.Common` | Pipeline behavior base, logging, DI patterns |
| `Domain.AI` | Agent manifests, skill definitions, tool declarations, telemetry conventions |
| `Microsoft.Agents.AI` | Agent framework (`AIAgent`, `ChatClientAgent`, `ChatClientAgentOptions`) |
| `Microsoft.Extensions.AI` | Chat client abstraction (`IChatClient`, `AITool`, `ChatOptions`) |
| `Azure.AI.Agents.Persistent` | Persistent agent storage API for AI Foundry provisioning |
| `Microsoft.Extensions.Caching.Abstractions` | `IDistributedCache` for middleware pipeline |

## Testing

Tests live in `Application.AI.Common.Tests`. Key test strategies:

- **Pipeline behaviors**: Mock the service interfaces (`ITextContentSafetyService`, `IToolPermissionService`), verify short-circuit on block/deny, verify metrics increment.
- **AgentFactory**: Mock `IChatClientFactory`, verify middleware pipeline is constructed correctly, verify error messages on missing providers.
- **ContextBudgetTracker**: Test allocation, warning thresholds, exhaustion detection.
- **AIToolConverter**: Verify JSON schema generation from `ITool` implementations.

```bash
dotnet test src/AgenticHarness.slnx --filter "FullyQualifiedName~Application.AI.Common"
```

Mock external AI services; test pipeline logic and conversion accuracy.
