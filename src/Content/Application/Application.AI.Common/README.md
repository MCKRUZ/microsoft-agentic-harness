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
                    │     Infrastructure.AI    │  (implements the 41 interfaces defined here)
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

This is an Application layer project, so it defines interfaces (contracts) and orchestration logic but never touches external services directly. It cannot reference HTTP clients, database connections, or AI SDK implementations -- only their abstractions. The 41 interfaces it defines are implemented by Infrastructure projects.

## Key Concepts

### The Agent-Specific Pipeline

When a request marked as agent-related flows through MediatR, it passes through AI-specific behaviors layered *on top of* Application.Common's generic pipeline:

```
Request enters
  → UnhandledExceptionBehavior     Outer safety net — catches, logs, enriches with agent context
    → AgentContextPropagation      Sets scoped agent identity (AgentId, ConversationId, TurnNumber)
      → AuditTrailBehavior         Records who did what, when, and the outcome
        → ContentSafetyBehavior    Screens IContentScreenable against safety policies
          → ToolPermissionBehavior 3-phase tool access check (rules → gates → rate limits)
            → GovernancePolicyBehavior  Evaluates governance policies
              → PromptInjectionBehavior Scans for injection attacks
                → HookBehavior     Fires pre/post lifecycle hooks
                  → RetrievalAuditBehavior  Records RAG retrieval metrics
                    → [Generic behaviors from Application.Common]
                      → Handler
```

Requests opt into specific behaviors via marker interfaces:

```csharp
// This command participates in content safety, tool permission, agent context, and observability:
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
5. Returns a ready-to-use agent

```csharp
// Creating an agent from a skill:
var agent = await agentFactory.CreateAgentFromSkillAsync("agents/research");

// Batch creation by category:
var analysts = await agentFactory.CreateAgentsByCategoryAsync("analysis");

// Provisioning a persistent agent on Azure AI Foundry:
var (agent, agentId) = await agentFactory.CreatePersistentAgentAsync(context);
```

**AgentExecutionContextFactory** maps a `SkillDefinition` to a runtime `AgentExecutionContext`. It resolves tools (MCP-first, then keyed DI fallback), assembles instructions, configures middleware, and sets deployment parameters.

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

### The 41 Interfaces

The interface surface defines the contracts that Infrastructure must implement:

| Category | Interfaces | Purpose |
|----------|-----------|---------|
| **Agent** | `IAgentFactory`, `IChatClientFactory`, `IAgentExecutionContext` | Agent creation and LLM client management |
| **Agents** | `IAgentMailbox`, `ISubagentProfileRegistry`, `ISubagentToolResolver` | Inter-agent messaging, subagent orchestration |
| **Compaction** | `IContextCompactionService`, `IAutoCompactStateMachine`, `ICompactionStrategyExecutor` | Context window reduction when budget exhausted |
| **Config** | `IConfigDiscoveryService` | Filesystem config file discovery |
| **Connectors** | `IConnectorClient`, `IConnectorClientFactory`, `ConnectorToolAdapter` | External service integrations as tools |
| **Context** | `IContextBudgetTracker`, `ITieredContextAssembler`, `IToolResultStore` | Token budget and progressive skill loading |
| **Governance** | `IGovernancePolicyEngine`, `IGovernanceAuditService`, `IMcpSecurityScanner`, `IPromptInjectionScanner` | Policy enforcement and threat detection |
| **Hooks** | `IHookExecutor`, `IHookRegistry` | Lifecycle event interception |
| **KnowledgeGraph** | `IKnowledgeGraphStore`, `IKnowledgeMemory`, `IFeedbackStore`, `IProvenanceStamper` | Graph-backed cross-session memory |
| **Memory** | `IAgentHistoryStore` | Conversation history persistence |
| **MetaHarness** | `IEvaluationService`, `IHarnessProposer`, `ISnapshotBuilder` | Automated harness optimization |
| **Permissions** | `IDenialTracker`, `IPatternMatcher`, `IPermissionRuleProvider`, `ISafetyGateRegistry` | Tool access control |
| **Prompts** | `ISystemPromptComposer`, `IPromptSectionProvider`, `IPromptSectionCache`, `IPromptCacheTracker` | System prompt assembly and caching |
| **RAG** | `IRagOrchestrator`, `IHybridRetriever`, `IVectorStore`, `IReranker`, `ICragEvaluator`, etc. | Full RAG pipeline (14 interfaces) |
| **Safety** | `ITextContentSafetyService` | Content screening |
| **Skills** | `ISkillContentProvider` | Skill resource loading |
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
├── DependencyInjection.cs             # Registers 9 behaviors, factories, services
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
├── Interfaces/                        # 41 contracts across 15 subdirectories
├── MediatRBehaviors/
│   ├── AgentContextPropagationBehavior.cs  # Sets scoped agent identity
│   ├── AuditTrailBehavior.cs          # Records who/what/when/outcome
│   ├── ContentSafetyBehavior.cs       # Input screening
│   ├── GovernancePolicyBehavior.cs    # Policy evaluation gate
│   ├── HookBehavior.cs               # Pre/post lifecycle hooks
│   ├── PromptInjectionBehavior.cs     # Injection attack detection
│   ├── RetrievalAuditBehavior.cs      # RAG retrieval metrics
│   ├── ToolPermissionBehavior.cs      # 3-phase permission check
│   └── UnhandledExceptionBehavior.cs  # Outer safety net
├── Middleware/
│   ├── ObservabilityMiddleware.cs     # Chat client middleware: tracks LLM calls
│   └── ToolDiagnosticsMiddleware.cs   # Chat client middleware: logs tool usage
├── Models/
│   ├── Context/ContextModels.cs       # Token allocation, budget status
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
    └── Tools/
        ├── AIToolConverter.cs         # ITool → AIFunction bridge
        └── ToolDescriptionBuilder.cs  # Generates LLM-friendly tool descriptions
```

## Key Types Reference

| Type | Purpose | Used By |
|------|---------|---------|
| **Factories** | | |
| `AgentFactory` | Creates AIAgent with middleware pipeline | ExecuteAgentTurnHandler |
| `AgentExecutionContextFactory` | SkillDefinition → AgentExecutionContext | AgentFactory |
| **Behaviors** | | |
| `ContentSafetyBehavior<,>` | Input screening via ITextContentSafetyService | IContentScreenable requests |
| `ToolPermissionBehavior<,>` | 3-phase tool access check | IToolRequest requests |
| `GovernancePolicyBehavior<,>` | Policy evaluation gate | All agent-scoped requests |
| `HookBehavior<,>` | Pre/post lifecycle hook dispatch | All requests (checks hook registry) |
| **Services** | | |
| `ContextBudgetTracker` | Tracks token allocation per agent | TieredContextAssembler, compaction |
| `TieredContextAssembler` | Loads skill tiers based on budget | AgentExecutionContextFactory |
| `AIToolConverter` | Converts ITool → AIFunction | AgentFactory tool wiring |
| **Interfaces** | | |
| `IRagOrchestrator` | Full RAG pipeline entry point | DocumentSearchTool, SearchDocumentsHandler |
| `ITool` | Framework-independent tool contract | All tool implementations |
| `IGovernancePolicyEngine` | Policy evaluation | GovernancePolicyBehavior |

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
