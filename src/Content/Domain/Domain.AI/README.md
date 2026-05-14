# Domain.AI

## What This Is

Domain.AI defines what an AI agent *is* in this system -- its identity, capabilities, knowledge, permissions, and observable behavior -- all as pure domain models with zero infrastructure dependencies. If Domain.Common is the shared language of the application, Domain.AI is the vocabulary of intelligence. It solves the problem of having agent concepts scattered across service implementations: by centralizing the domain model, every layer agrees on what a "skill" means, what a "tool declaration" contains, and what telemetry attributes to emit.

This project sits one level above Domain.Common in the dependency graph. Every Application-layer project that deals with agents (Application.AI.Common, Application.Core) depends on it directly. Infrastructure projects (Infrastructure.AI, Infrastructure.AI.RAG, Infrastructure.AI.Governance) implement against the models defined here. Domain.AI itself depends only on Domain.Common and two Microsoft abstractions packages.

## Architecture Context

```
                    ┌─────────────────────────┐
                    │     Presentation         │
                    └────────────┬────────────┘
                                 │
                    ┌────────────▼────────────┐
                    │     Infrastructure       │  (implements against Domain.AI models)
                    └────────────┬────────────┘
                                 │
                    ┌────────────▼────────────┐
                    │      Application         │  (uses Domain.AI as its type system)
                    └────────────┬────────────┘
                                 │
                ╔════════════════▼════════════════╗
                ║          Domain.AI             ║  ← YOU ARE HERE
                ╠════════════════╤════════════════╣
                                 │
                    ┌────────────▼────────────┐
                    │      Domain.Common       │
                    └─────────────────────────┘
```

As a Domain project, Domain.AI contains only entities, value objects, enums, and semantic constants. It has no behavior beyond simple validation methods. It cannot reference MediatR, Entity Framework, HTTP clients, or any AI SDK *implementation* -- only the `Microsoft.Extensions.AI.Abstractions` contract for the `AITool` type, and `Microsoft.Agents.AI.Abstractions` for the agent framework interop type.

## Key Concepts

### Agents and Manifests

An agent in this system is more than a system prompt. It is a composite of identity, capabilities, and constraints described by an `AGENT.md` file on disk.

**AgentDefinition** is the lightweight "index card" -- just enough metadata to list agents in a UI or select one for invocation, without loading the full manifest body.

**AgentManifest** is the fully-parsed blueprint: instructions, tool access lists, workflow state configuration, decision frameworks, and skill references.

**AgentExecutionContext** is the *runtime form* of a manifest -- the concrete instructions, resolved tool instances, middleware list, and deployment config needed to actually run an agent.

```csharp
// How the factory uses these types (in Application.AI.Common):
var context = new AgentExecutionContext
{
    Name = "research-agent",
    Instruction = manifest.Instructions,
    Tools = resolvedTools,
    DeploymentName = "gpt-4o",
    AIAgentFrameworkType = AIAgentFrameworkClientType.AzureOpenAI
};
var agent = await agentFactory.CreateAgentAsync(context);
```

### Skills: Progressive Disclosure

Skills are the harness's answer to "how does an agent know what it knows without blowing its context budget?" The `SkillDefinition` class models a three-tier progressive disclosure system:

- **Tier 1 (Index Card)**: ~100 tokens. ID, Name, Description, Category, Tags. Always in memory.
- **Tier 2 (Folder)**: ~5,000 tokens. Full instructions body. Loaded when the skill is selected.
- **Tier 3 (Filing Cabinet)**: Unbounded. Templates, references, scripts. Loaded only during execution.

Without this tiering, an agent with 20 skills would burn 100K+ tokens on instructions alone before the user said anything.

```csharp
// Checking tier sizes during skill development:
var skill = skillRegistry.TryGet("agents/research");
Console.WriteLine($"Tier 1: {skill.Level1TokenEstimate} tokens");   // ~80
Console.WriteLine($"Tier 2: {skill.Level2TokenEstimate} tokens");   // ~4200
Console.WriteLine($"Tier 3: {skill.TotalResourceCount} resources"); // 6 files
Console.WriteLine($"Oversized? {skill.IsLevel2Oversized}");         // false (under 5K)
```

### Tool Declarations

A `ToolDeclaration` describes what a skill says it needs -- not the implementation, just the requirement. It names the tool, lists specific operations, defines fallback behavior, and carries usage guidance.

```yaml
# From a SKILL.md frontmatter:
tools:
  - name: azure_devops_work_items
    operations: [create_sprint, create_work_item]
    fallback: jira_issues
    optional: true
    when_to_use: "Creating work items from analysis results"
    when_not_to_use: "Reading existing items (use query tool instead)"
```

The `ToolConcurrencyClassification` enum (`ReadOnly`, `WriteSerial`, `Unknown`) lets the harness safely parallelize batched tool calls.

### Permissions Model

The permission system is built for defense in depth. `ToolPermissionRule` defines allow/deny/ask rules with priority ordering. `SafetyGate` marks paths that are always dangerous regardless of permission mode. `DenialRecord` tracks denial history for rate limiting.

```csharp
// A rule from agent manifest:
var rule = new ToolPermissionRule
{
    ToolPattern = "file_system",
    OperationPattern = "write",
    Behavior = PermissionBehaviorType.Ask,    // Requires user confirmation
    Source = PermissionRuleSource.AgentManifest,
    Priority = 100
};
```

Rules are evaluated in priority order across 8 possible sources (AgentManifest, SkillDefinition, UserSettings, ProjectSettings, LocalSettings, SessionOverride, PolicySettings, CliArgument).

### Hooks (Lifecycle Events)

Hooks let external code intercept the agent at 16 lifecycle points. A `HookDefinition` subscribes to an event with an execution mechanism (Command, Prompt, Middleware, Http).

```csharp
var hook = new HookDefinition
{
    Event = HookEvent.PreToolUse,
    Type = HookType.Command,
    ToolMatcher = "file_system:write",  // Only triggers for file writes
    Timeout = TimeSpan.FromSeconds(5),
    RunOnce = false
};
```

`HookResult` returns either `Continue` (let the operation proceed, optionally with modified input) or `Block` (short-circuit the entire pipeline).

### Context and Compaction

When an agent's context window fills up, it needs strategies to compress. The domain models define three compaction algorithms via `CompactionStrategy`: Full (LLM summarizes everything), Partial (compress old, keep recent), and Micro (trim individual tool results).

`TokenBudgetDecision` is the enum that drives loading decisions: should the system load a skill at Tier 2, trigger compaction, or fall back to Index Cards only?

### RAG Pipeline Models

The Retrieval-Augmented Generation domain includes:

- `DocumentChunk` -- A discrete text unit with embedding, section path, and provenance metadata
- `ChunkMetadata` -- Source URI, contextual prefix, sibling/parent relationships
- `CragEvaluation` -- Corrective RAG quality scoring (accept/refine/reject)
- `RetrievalResult` / `RerankedResult` / `RagAssembledContext` -- Pipeline stage outputs
- Enums: `ChunkingStrategy`, `RetrievalStrategy`, `QueryType`, `VectorStoreProvider`

### Telemetry Conventions

Fifteen convention classes define semantic attribute names for OpenTelemetry. These ensure consistent, queryable telemetry across every subsystem:

```csharp
// Using conventions in a span:
activity?.SetTag(AgentConventions.Name, "research-agent");
activity?.SetTag(AgentConventions.TurnIndex, 3);
activity?.SetTag(ToolConventions.Name, "file_system");
activity?.SetTag(SafetyConventions.Outcome, SafetyConventions.OutcomeValues.Pass);
```

Covers: Agent, Budget, Compaction, Context, Governance, Hook, MCP, Orchestration, Permission, RAG, Safety, Session, Token, Tool, User.

### Governance

`GovernanceDecision` is the outcome of policy evaluation -- whether an action is allowed or denied, which rule matched, and timing metrics. `GovernancePolicyAction` and `GovernancePolicyScope` define the policy taxonomy.

## Project Structure

```
Domain.AI/
├── A2A/                         # AgentCard — agent discovery protocol model
├── Agents/
│   ├── AgentDefinition.cs       # Lightweight index card for agent discovery
│   ├── AgentExecutionContext.cs # Runtime agent configuration (tools, instructions, deployment)
│   ├── AgentManifest.cs         # Full parsed AGENT.md blueprint
│   ├── AgentMessage.cs          # Inter-agent mailbox message (Task/Result/Notification/Error)
│   ├── SkillReference.cs        # Pointer from agent to skill
│   └── SubagentDefinition.cs    # Child agent spec (type, tools, limits, model override)
├── Compaction/                  # CompactionStrategy, CompactionResult, BoundaryMessage, Trigger
├── Config/                      # ConfigScope (priority), DiscoveredConfigFile
├── Context/                     # TokenBudgetDecision, ToolResultReference
├── Enums/                       # AgentDirectory (well-known paths)
├── Governance/                  # GovernanceDecision, PolicyAction, PolicyScope, InjectionScanResult
├── Hooks/                       # HookDefinition, HookEvent (16 events), HookResult, ExecutionContext
├── KnowledgeGraph/Models/       # GraphNode, GraphEdge, GraphTriplet, ProvenanceStamp, FeedbackWeights
├── MCP/                         # McpPrompt, McpResource, McpRequestContext
├── Models/                      # AgentRunManifest, ContentSafetyResult, ToolResult, FileSearchResult
├── Observability/Models/        # AuditEntry, SessionRecord, ToolExecutionRecord, SafetyEventRecord
├── Permissions/                 # ToolPermissionRule, PermissionDecision, SafetyGate, DenialRecord
├── Prompts/                     # SystemPromptSection, PromptHashSnapshot, PromptCacheBreakReport
├── RAG/
│   ├── Enums/                   # ChunkingStrategy, RetrievalStrategy, QueryType, VectorStoreProvider
│   └── Models/                  # DocumentChunk, CragEvaluation, RagAssembledContext, CitationSpan
├── Skills/
│   ├── SkillDefinition.cs       # 3-tier progressive disclosure model
│   ├── ContextContract.cs       # Input/output requirements
│   ├── ContextLoading.cs        # Per-tier loading rules
│   ├── SkillResource.cs         # Attached file (template, reference, script, asset)
│   └── SkillAgentOptions.cs     # Skill-to-agent mapping options
├── Telemetry/Conventions/       # 15 convention classes (semantic OTel attributes)
└── Tools/
    ├── ToolDeclaration.cs       # Skill's tool requirement (name, ops, fallback, guidance)
    └── ToolConcurrencyClassification.cs  # ReadOnly | WriteSerial | Unknown
```

## Key Types Reference

| Type | Purpose | Used By |
|------|---------|---------|
| **Agents** | | |
| `AgentDefinition` | Discovery metadata for UI listing | IAgentMetadataRegistry, AgentHub |
| `AgentManifest` | Full parsed AGENT.md | AgentFactory, skill loading |
| `AgentExecutionContext` | Runtime config passed to agent factory | AgentFactory.CreateAgentAsync |
| `SubagentDefinition` | Child agent spec | RunOrchestratedTask handler |
| **Skills** | | |
| `SkillDefinition` | 3-tier skill model | ISkillMetadataRegistry, TieredContextAssembler |
| `ContextContract` | Input/output declaration | Context budget decisions |
| `SkillResource` | Attached file artifact | Skill content providers |
| **Tools** | | |
| `ToolDeclaration` | Skill's tool requirement | AgentExecutionContextFactory |
| `ToolConcurrencyClassification` | Parallelism safety | IToolConcurrencyClassifier |
| **Permissions** | | |
| `ToolPermissionRule` | Allow/deny/ask rule | IPermissionRuleProvider |
| `PermissionDecision` | Resolved permission outcome | ToolPermissionBehavior |
| `SafetyGate` | Always-dangerous paths | ISafetyGateRegistry |
| **RAG** | | |
| `DocumentChunk` | Text unit with embedding | Vector stores, retrieval pipeline |
| `RagAssembledContext` | Final assembled RAG output | IRagOrchestrator consumers |
| **Telemetry** | | |
| `AgentConventions` | Span attribute constants | All instrumentation code |
| `SafetyConventions` | Safety metric tags | ContentSafetyBehavior |

## Common Tasks

### How to Add a New Telemetry Convention

1. Create a static class in `Telemetry/Conventions/`:

```csharp
namespace Domain.AI.Telemetry.Conventions;

public static class MyFeatureConventions
{
    public const string OperationType = "my_feature.operation.type";
    public const string Duration = "my_feature.duration_ms";
    public const string Success = "my_feature.success";
}
```

2. Use these constants in your metrics/spans (in Application or Infrastructure layers):

```csharp
activity?.SetTag(MyFeatureConventions.OperationType, "ingest");
myCounter.Add(1, new(MyFeatureConventions.Success, true));
```

### How to Add a New Hook Event

1. Add the event to the `HookEvent` enum in `Hooks/HookEvent.cs`
2. The `IHookExecutor` (in Application.AI.Common) will automatically support firing hooks for the new event
3. Fire it from the relevant handler or behavior:

```csharp
await hookExecutor.FireAsync(HookEvent.MyNewEvent, new HookExecutionContext { ... });
```

### How to Add a New RAG Domain Model

1. Place the record/class in `RAG/Models/` or `RAG/Enums/` as appropriate
2. If it represents a pipeline stage output, ensure it's immutable (use `record` with `required` properties)
3. Reference it from the corresponding interface in `Application.AI.Common/Interfaces/RAG/`

## Dependencies

| Reference | Why |
|-----------|-----|
| `Domain.Common` | Result pattern, AppConfig hierarchy, workflow state models |
| `Microsoft.Extensions.AI.Abstractions` | `AITool` type for tool interop (the harness's `ITool` bridges to this) |
| `Microsoft.Agents.AI.Abstractions` | Agent framework type contracts |

Nothing else. The domain layer stays pure -- all implementation lives in Infrastructure.

## Testing

Tests for Domain.AI live in `Domain.AI.Tests`. Focus on:
- `SkillDefinition` token estimation accuracy
- `DecisionFramework.Validate()` rule completeness checks
- `ToolDeclaration` computed properties
- Enum completeness and string conventions

```bash
dotnet test src/AgenticHarness.slnx --filter "FullyQualifiedName~Domain.AI"
```

All tests are pure unit tests -- no mocking needed since the domain layer has no external dependencies.
