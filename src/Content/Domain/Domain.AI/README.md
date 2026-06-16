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

### Skills: Progressive Disclosure and Dual-Mode Execution

Skills are the harness's answer to "how does an agent know what it knows without blowing its context budget?" The `SkillDefinition` class models a three-tier progressive disclosure system:

- **Tier 1 (Index Card)**: ~100 tokens. ID, Name, Description, Category, Tags. Always in memory.
- **Tier 2 (Folder)**: ~5,000 tokens. Full instructions body. Loaded when the skill is selected.
- **Tier 3 (Filing Cabinet)**: Unbounded. Templates, references, scripts. Loaded only during execution.

Without this tiering, an agent with 20 skills would burn 100K+ tokens on instructions alone before the user said anything.

**Skill Modes**: Skills operate in two modes via the `SkillMode` enum. `Managed` skills are harness-native with explicit tool declarations -- the harness resolves only the tools the skill names. `Injected` skills come from plugins and receive all MCP tools from their plugin's servers, bypassing explicit tool declarations.

**Prerequisites**: Skills can declare `Prerequisites` (a list of skill IDs that must complete first) and a `CompletionTool` (the tool name whose invocation marks the skill as complete). This enables ordered skill composition within a single agent run.

**Multi-Skill Agents**: `AgentDefinition.Skills` is an `IReadOnlyList<SkillReference>` -- agents declare multiple skills, not just one. At context assembly time, instructions are merged and tools combined from all referenced skills.

```csharp
// Skills support two modes:
// Managed: harness-native skills with explicit tool declarations
// Injected: plugin-provided skills that get all MCP tools passed through
var skill = skillRegistry.TryGet("agents/research");
Console.WriteLine($"Mode: {skill.Mode}");              // Managed or Injected
Console.WriteLine($"Plugin: {skill.PluginSource}");     // null for Managed skills
Console.WriteLine($"Prerequisites: {skill.Prerequisites.Count}"); // skills that must complete first
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

Rules are evaluated in priority order across 9 possible sources (AgentManifest, SkillDefinition, UserSettings, ProjectSettings, LocalSettings, SessionOverride, PolicySettings, CliArgument, PluginDeclaration). The `PluginDeclaration` source was added to support plugin-level autonomy and denied-tool rules that feed into the 3-phase resolver alongside agent-level rules.

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

### Planner: DAG-Based Plan Execution

The planner subsystem models executable plans as directed acyclic graphs. A `PlanGraph` is the root aggregate containing steps (nodes) and edges (directed connections), with plan-level configuration controlling timeouts, parallelism, and recursion depth.

**PlanGraph** is the root entity. It owns an ordered list of `PlanStep` nodes and `PlanEdge` connections, along with a `PlanConfiguration` for plan-level limits. Top-level plans have a null `ParentPlanId`; sub-plans reference their parent.

**PlanStep** is a single executable unit of work in the graph. Its `StepType` determines which keyed executor handles it at runtime, and its `StepConfiguration` provides the type-specific parameters. Each step carries its own `RetryPolicy`, `Timeout`, and an optional `RequiredAutonomyLevel` that gates execution behind governance checks.

**PlanEdge** is a directed connection between two steps. Four `EdgeType` values classify the relationship: `DataFlow` (output feeds input), `ControlFlow` (sequencing), `ConditionalTrue`, and `ConditionalFalse` (branching). An optional `Condition` expression is evaluated at runtime for conditional edges.

```csharp
// Building a simple two-step plan:
var step1 = new PlanStep
{
    Id = PlanStepId.New(),
    Name = "Analyze input",
    Type = StepType.LlmCall,
    Configuration = new LlmCallConfig
    {
        SystemPrompt = "Analyze the user request.",
        ModelDeploymentKey = "gpt-4o"
    },
    RetryPolicy = new RetryPolicy { MaxRetries = 2 }
};
var step2 = new PlanStep
{
    Id = PlanStepId.New(),
    Name = "Execute tool",
    Type = StepType.ToolUse,
    Configuration = new ToolUseConfig { ToolName = "file_system" },
    RetryPolicy = new RetryPolicy { OnExhausted = ErrorRecovery.Escalate }
};
var plan = new PlanGraph
{
    Id = PlanId.New(),
    Name = "Analyze and act",
    Steps = [step1, step2],
    Edges = [new PlanEdge(step1.Id, step2.Id, EdgeType.ControlFlow)],
    Configuration = new PlanConfiguration { MaxParallelSteps = 5 }
};
```

**StepType** determines which keyed `IPlanStepExecutor` handles the step: `LlmCall` (LLM inference), `ToolUse` (sandbox-routed tool), `HumanGate` (approval gate), `ConditionalBranch` (expression evaluation), and `SubPlanInvocation` (nested plan with depth limiting).

**StepConfiguration** is a polymorphic base record with five concrete subtypes, serialized via `[JsonPolymorphic]` with a `type` discriminator:

| Config Type | Key Properties |
|---|---|
| `LlmCallConfig` | SystemPrompt, ModelDeploymentKey, Temperature, MaxTokens |
| `ToolUseConfig` | ToolName, InputParameters, IsolationLevelOverride |
| `HumanGateConfig` | EscalationMessage, ApprovalStrategy (AnyOf/AllOf/Quorum), Approvers, RiskLevel, Timeout |
| `ConditionalBranchConfig` | ConditionExpression, TrueEdgeTargetId, FalseEdgeTargetId |
| `SubPlanConfig` | ChildPlanId (reference) or InlinePlanDefinition (inline graph), IsolateContext |

**RetryPolicy** configures retry behavior: `MaxRetries`, `InitialDelay`, `BackoffStrategy` (Fixed/Linear/Exponential), and `OnExhausted` which is an `ErrorRecovery` enum determining what happens after all retries fail: `FailStep`, `SkipStep`, `FailPlan`, or `Escalate`.

**StepExecutionStatus** is the state machine for step lifecycle: Pending -> Ready -> Running -> Completed | Failed | Skipped. `Blocked` is entered from Ready when awaiting external input (human gate). `Cancelled` is entered on explicit cancellation.

**StepExecutionState** tracks runtime state per step: `StepId`, `Status`, `AttemptCount`, `StartedAt`/`CompletedAt` timestamps, `Output`, `ErrorMessage`, and an optional HMAC-signed `ToolExecutionAttestation`.

**StepExecutionResult** is what step executors return: `Status`, `Output`, `ErrorMessage`, `Duration`, optional `Attestation`, and `ActiveEdgeTarget` for conditional branch steps.

**PlanExecutionSummary** is the final result of a plan run: `PlanId`, `FinalStatus`, `TotalDuration`, per-step `StepStates`, and aggregate counts (`CompletedStepCount`, `FailedStepCount`, `SkippedStepCount`).

**PlanExecutionLogEntry** provides audit trail entries: `PlanId`, `StepId`, `Timestamp`, `Status`, `Message`, and `AttemptNumber`.

Supporting types: `PlanId` and `PlanStepId` are strongly-typed GUID wrappers preventing primitive obsession. `PlanConfiguration` controls plan-level `PlanTimeout`, `MaxParallelSteps`, and `MaxSubPlanDepth`. `PlanExecutionContext` tracks runtime recursion depth for sub-plan limiting. `PlanGenerationConstraints` bounds LLM-driven plan generation (max steps, allowed types, max depth, max timeout). `PlanValidationResult` reports validation outcome with errors, warnings, and estimated critical path duration.

### Sandbox: Isolated Tool Execution

The sandbox subsystem defines the security and resource boundary for tool execution. Every tool runs through a sandbox that enforces capability checks, resource limits, and isolation levels.

**SandboxExecutionRequest** encapsulates all inputs for sandboxed execution: `ToolName`, `Input` (serialized), `Limits` (resource constraints), `PermissionProfile` (capability and access rules), and `Timeout`. For process-level execution, `Command` names the executable and `ArgumentList` provides arguments as individual entries (preferred over the deprecated `Arguments` string to prevent shell injection).

**SandboxExecutionResult** is the outcome: `Success` flag, `Output`, `ErrorMessage`, `ExitCode`, `ResourceUsage` (actual consumption metrics), and an HMAC-signed `ToolExecutionAttestation`.

```csharp
// Building a sandbox request:
var request = new SandboxExecutionRequest
{
    ToolName = "code_analysis",
    Input = "{\"file\": \"Program.cs\"}",
    Limits = new ResourceLimits { MemoryLimitBytes = 128 * 1024 * 1024 },
    PermissionProfile = new ToolPermissionProfile
    {
        RequiredCapabilities = ToolCapability.FileRead | ToolCapability.Subprocess,
        AllowedPaths = ["/workspace/src"],
        DeniedPaths = ["/workspace/.env"],
        MinimumIsolation = SandboxIsolationLevel.Process
    },
    ArgumentList = ["--analyze", "Program.cs"],
    Timeout = TimeSpan.FromSeconds(15)
};
```

**SandboxIsolationLevel** defines three levels with intentional numeric ordering (`None < Process < Container`) used by the capability enforcer for never-downgrade checks: `None` (direct execution for safe read-only tools), `Process` (subprocess with Job Object limits -- the default), `Container` (Docker with full filesystem, network, memory, and CPU isolation).

**ToolCapability** is a flags enum declaring what a tool needs to execute. The capability enforcer verifies these requirements before allowing execution. Values: `None`, `FileRead`, `FileWrite`, `NetworkAccess`, `Subprocess`, `EnvRead`, `DatabaseRead`, `DatabaseWrite`, `LlmInvocation`.

**ToolPermissionProfile** declares a tool's full access scope with deny-overrides-allow semantics: `RequiredCapabilities`, `AllowedPaths`/`DeniedPaths`, `AllowedHosts`/`DeniedHosts`, `AllowedPrograms`, and `MinimumIsolation`.

**ToolCapabilityAttribute** (`[ToolCapability(ToolCapability.FileRead)]`) is a class-level attribute for declaring capabilities at compile time. Can be overridden at runtime via appsettings configuration.

**ResourceLimits** defines hard caps enforced via Job Objects (Windows) or cgroups (Linux): `MemoryLimitBytes` (default 256 MB), `CpuTimeSeconds` (default 30s), `MaxSubprocesses` (default 5), `DiskQuotaBytes` (default 100 MB).

**ResourceUsage** captures actual consumption during execution: `MemoryBytes`, `CpuTimeSeconds`, `WallClockDuration`, `SubprocessCount`, `DiskUsageBytes`.

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
├── Governance/                  # GovernanceDecision, PolicyAction, PolicyScope, InjectionScanResult
├── Hooks/                       # HookDefinition, HookEvent (16 events), HookResult, ExecutionContext
├── KnowledgeGraph/Models/       # GraphNode, GraphEdge, GraphTriplet, ProvenanceStamp, FeedbackWeights
├── MCP/                         # McpPrompt, McpResource, McpRequestContext
├── Models/                      # AgentRunManifest, ContentSafetyResult, ToolResult, FileSearchResult
├── Observability/Models/        # AuditEntry, SessionRecord, ToolExecutionRecord, SafetyEventRecord
├── Permissions/                 # ToolPermissionRule, PermissionDecision, SafetyGate, DenialRecord
├── Planner/                     # PlanGraph, PlanStep, PlanEdge, StepConfiguration hierarchy, execution state
├── Prompts/                     # SystemPromptSection, PromptHashSnapshot, PromptCacheBreakReport
├── RAG/
│   ├── Enums/                   # ChunkingStrategy, RetrievalStrategy, QueryType, VectorStoreProvider
│   └── Models/                  # DocumentChunk, CragEvaluation, RagAssembledContext, CitationSpan
├── Sandbox/                     # SandboxExecutionRequest/Result, ToolCapability, ToolPermissionProfile, ResourceLimits
├── Compression/
│   ├── Enums/
│   │   └── ToolOutputCategory.cs    # Default | Diagnostic | DataHeavy | Conversational
│   └── Models/
│       └── CompressionResult.cs     # Compression outcome with strategy and truncation info
├── Skills/
│   ├── SkillDefinition.cs       # Skill metadata with dual-mode support (Managed/Injected)
│   ├── SkillMode.cs             # Managed (harness-native) | Injected (plugin pass-through)
│   ├── SkillResource.cs         # Attached file (template, reference, script, asset)
│   └── SkillAgentOptions.cs     # Skill-to-agent mapping options (AdditionalTools)
├── SkillTraining/               # SkillOpt-port value types for the skill-document training loop
│   ├── EditOp.cs                # Append | InsertAfter | Replace | Delete
│   ├── Edit.cs                  # Single bounded edit against a skill doc
│   ├── Patch.cs                 # Ordered batch of edits + reasoning
│   ├── PatchApplyReport.cs      # Applied vs failed edits + content-equality HasChanges flag
│   ├── SourceType.cs            # Failure | Success origin classification
│   ├── GateMetric.cs            # Hard | Soft | Mixed projection
│   ├── GateAction.cs            # AcceptNewBest | Accept | Reject
│   ├── GateResult.cs            # Immutable gate outcome with current/best/candidate state
│   ├── RolloutResult.cs         # Per-item rollout outcome (Hard, Soft, Trajectory) + IsSuccess epsilon
│   ├── RolloutBatch.cs          # Split-aware rollout request (train/val/test, ids, seed)
│   ├── ReflectionInput.cs       # Bundle the optimizer reflects on
│   ├── SlowUpdateAnalysis.cs    # Paired longitudinal counts + guidance string
│   ├── SkillTrainingCheckpoint.cs    # Per-step snapshot (run, skill content + SHA-256 hash, score, action)
│   ├── SkillTrainingRunResult.cs     # Final run outcome with per-step audit trail + HasAcceptedAny flag
│   └── TrainSkillConfig.cs      # Epochs, steps, LR schedule, gate metric, patience, slow-update/meta toggles
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
| `SkillDefinition` | Skill metadata with dual-mode (Managed/Injected) and prerequisites | ISkillMetadataRegistry, AgentExecutionContextFactory |
| `SkillMode` | Managed (harness-native) vs Injected (plugin) mode | AgentExecutionContextFactory |
| `SkillResource` | Attached file artifact | Skill content providers |
| **Tools** | | |
| `ToolDeclaration` | Skill's tool requirement | AgentExecutionContextFactory |
| `ToolConcurrencyClassification` | Parallelism safety | IToolConcurrencyClassifier |
| **Permissions** | | |
| `ToolPermissionRule` | Allow/deny/ask rule | IPermissionRuleProvider |
| `PermissionDecision` | Resolved permission outcome | ToolPermissionBehavior |
| `SafetyGate` | Always-dangerous paths | ISafetyGateRegistry |
| **Planner** | | |
| `PlanGraph` | Root aggregate: DAG of steps and edges | IPlanExecutor, IPlanGenerator |
| `PlanStep` | Executable node with type-specific config | IPlanStepExecutor (keyed by StepType) |
| `PlanEdge` | Directed edge (DataFlow/ControlFlow/Conditional) | Plan graph traversal, scheduler |
| `StepConfiguration` | Polymorphic base for step configs | JSON serialization, step executors |
| `StepExecutionState` | Runtime state per step | PlanExecutionSummary, audit trail |
| `RetryPolicy` | Retry + backoff + error recovery | PlanStep, step executors |
| `PlanExecutionSummary` | Final plan result with step counts | IPlanExecutor consumers |
| `PlanValidationResult` | Pre-execution validation outcome | IPlanValidator |
| **Sandbox** | | |
| `SandboxExecutionRequest` | Inputs for sandboxed tool execution | ISandboxExecutor |
| `SandboxExecutionResult` | Outcome with attestation and resource usage | ISandboxExecutor consumers |
| `ToolCapability` | Flags enum of required capabilities | ToolPermissionProfile, capability enforcer |
| `ToolPermissionProfile` | Access scope with deny-overrides-allow | ISandboxExecutor, ToolUseConfig |
| `ResourceLimits` | Hard caps on memory, CPU, processes, disk | SandboxExecutionRequest |
| `ToolCapabilityAttribute` | Compile-time capability declaration | Tool class annotations |
| **RAG** | | |
| `DocumentChunk` | Text unit with embedding | Vector stores, retrieval pipeline |
| `RagAssembledContext` | Final assembled RAG output | IRagOrchestrator consumers |
| **Telemetry** | | |
| `AgentConventions` | Span attribute constants | All instrumentation code |
| `SafetyConventions` | Safety metric tags | ContentSafetyBehavior |
| **Compression** | | |
| `ToolOutputCategory` | Classification for compression strategy selection | ITool, ToolOutputCompressor |
| `CompressionResult` | Compression outcome with strategy used | ICompressionStrategy implementations |

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
- `SkillDefinition` mode computation (`Managed` vs `Injected`) and prerequisite validation
- `SkillMode` enum coverage
- `DecisionFramework.Validate()` rule completeness checks
- `ToolDeclaration` computed properties
- Enum completeness and string conventions

```bash
dotnet test src/AgenticHarness.slnx --filter "FullyQualifiedName~Domain.AI"
```

All tests are pure unit tests -- no mocking needed since the domain layer has no external dependencies.
