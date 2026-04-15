# Research Findings: Meta-Harness Implementation

## Part 1: Codebase Analysis

### Harness Project — Layer Structure

- **Domain**: `Domain.Common` (config, models)
- **Application**: `Application.Common`, `Application.AI.Common`, `Application.Core`
- **Infrastructure**: `Infrastructure.Common`, `Infrastructure.AI`, `Infrastructure.AI.Connectors`, `Infrastructure.AI.MCP`, `Infrastructure.APIAccess`, `Infrastructure.Observability`
- **Presentation**: `Presentation.Common`, `Presentation.ConsoleUI`

### Existing Interfaces in Application.AI.Common/Interfaces/

- **Agent**: `IAgentExecutionContext`, `IAuditSink`, `ITextContentSafetyService`, `IToolPermissionService`
- **Coordination**: `IAgentMailbox`, `ISubagentProfileRegistry`, `ISubagentToolResolver`
- **Compaction**: `IAutoCompactStateMachine`, `ICompactionStrategyExecutor`, `IContextCompactionService`
- **Config**: `IConfigDiscoveryService`
- **Connectors**: `IConnectorClient`, `IConnectorClientFactory`, `ConnectorToolAdapter`, `ConnectorOperationResult`
- **A2A**: `IA2AAgentHost`
- **Tools**: `IFileSystemService`, `ITool`

**No existing `IExecutionTraceStore`, `IAgentHistoryStore`, `IHarnessProposer`, or `IHarnessCandidateRepository` — all are new.**

### DI Patterns

Keyed registration pattern used throughout:
```csharp
services.AddKeyedSingleton<ITool>(FileSystemTool.ToolName, (sp, _) =>
    new FileSystemTool(sp.GetRequiredService<IFileSystemService>()));
```

Multi-layer DI chain: `Application.Common` → `Application.AI.Common` → `Application.Core` → `Infrastructure.AI`

Key existing services: `IChatClientFactory`, `ISkillMetadataRegistry`, `IToolConcurrencyClassifier`, `IToolExecutionStrategy`, `IStateMarkdownGenerator`, `JsonCheckpointStateManager`, `CompositeStateManager`, `IPermissionRuleProvider`, `IHookRegistry`, `ISystemPromptComposer`

### CQRS Pattern (Application.Core/CQRS/Agents/)

Existing handlers: `RunOrchestratedTaskCommandHandler`, `RunConversationCommandHandler`, `ExecuteAgentTurnCommandHandler`

Pattern:
- Implements `IRequestHandler<TCommand, TResponse>` (MediatR)
- Constructor injection of dependencies
- Returns structured result types (`OrchestratedTaskResult`, `AgentTurn`)
- `RunOrchestratedTaskCommandHandler` parses responses for `SUBTASK:` patterns, delegates to sub-agents via keyed DI, synthesizes results

### AppConfig Hierarchy

```
AppConfig
├── Common (SlowThresholdSec)
├── Logging (LogsBasePath, PipeName, SuppressConsoleOutput)
├── Agent (MaxTurnsPerConversation, DefaultTokenBudget)
├── Http (CORS, Authorization)
├── Agent → Skills, Permissions, MCP, Orchestration
├── Infrastructure → FileSystem (AllowedBasePaths)
├── AI → AgentFramework, McpServers, Skills, AIFoundry
├── Observability → EnableTracing, EnableMetrics, SamplingRatio
└── Cache → CacheType
```

Config bound via `IOptionsMonitor<AppConfig>`. Paths resolved from `AppContext.BaseDirectory`.

### FileSystemService (Infrastructure.AI/Tools/)

Sandboxed file operations with base-path restrictions:
- `ReadFileAsync`, `WriteFileAsync`, `ListDirectoryAsync`, `SearchFilesAsync`, `ExistsAsync`
- 10 MB read limit, 100 results max for search
- Blocks system directories, validates against allowed base paths
- Skips: .git, node_modules, bin, obj, .vs, .vscode, .claude, .worktrees

### OpenTelemetry Setup

- Registered in `Infrastructure.Observability/DependencyInjection.cs`
- Custom span processor enriches spans with AI context (agent names, turn indices, conversation IDs)
- Recognizes `Microsoft.Extensions.AI`, `Semantic Kernel`, `Azure.AI.OpenAI` activity sources
- Exports to Jaeger + Prometheus; structured JSON logging
- Config: `AppConfig.Observability` (EnableTracing, EnableMetrics, SamplingRatio)

### Test Patterns

- xUnit, interface-based mocking with Moq
- `Application.AI.Common.Tests`, `Application.Core.Tests`, `Infrastructure.AI.Tests` exist
- Tests in parallel namespaced directories mirroring source

---

### ApplicationTemplate — Relevant Patterns

#### IArtifactStorageService (Application.AI.Common/Interfaces/Tools/)
```csharp
StoreArtifactAsync(artifactPath, content) → URI
GetArtifactAsync(artifactPath) → content
ListArtifactsAsync(directoryPath) → paths
DeleteArtifactAsync(artifactPath) → success
IsAvailable property
```
**This is the closest existing analogue to `IExecutionTraceStore`.** The new interface should follow this pattern but add run/turn structure.

#### IStateManager (Domain.Common/Workflow/)
Generic workflow state persistence:
- `LoadAsync(workflowId)` / `SaveAsync(state)`
- Node operations: `GetNodeStateAsync`, `UpdateNodeStateAsync`
- Status transitions: `CanTransitionAsync`, `TransitionAsync`
- Metadata: `SetMetadataAsync<T>`, `GetMetadataAsync<T>`

Implementations: `JsonCheckpointStateManager`, `MarkdownStateManager`, `CompositeStateManager`

**The harness candidate repository should follow this pattern — generic, not hardcoded to specific phases.**

---

## Part 2: Web Research Findings

### Topic 1: Execution Trace Storage Patterns

**JSONL is the right format** for per-run execution artifacts. Each line is a self-contained JSON object (one span or one turn). Supports `grep`, `jq`, `ripgrep` natively. JSON (single object) requires loading the whole file.

**Recommended directory structure** (from Meta-Harness paper + LangSmith patterns):
```
{runs_root}/
  {run_id}/
    manifest.json          # Run metadata (runId, startTime, harnessVersion, candidateId)
    scores.json            # Aggregate scores by example
    summary.md             # Human-readable outcome
    traces.jsonl           # One line per turn/span (append-only)
    turns/
      {turn_n}/
        system_prompt.md
        tool_calls.jsonl
        model_response.md
        state_snapshot.json
```

**Key principle from Meta-Harness**: "Log everything in a format easy to navigate. Code should write code, scores, and execution traces in a form that `grep` and `cat` can navigate without reconstruction."

Sources: LangSmith docs, DSPy docs, arXiv:2603.28052

### Topic 2: OpenTelemetry Causal Span Attribution (.NET 2025)

GenAI semantic conventions are in **Development** status (opt-in via `OTEL_SEMCONV_STABILITY_OPT_IN=gen_ai_latest_experimental`).

**Standard span types:**

| Operation | `gen_ai.operation.name` | Span name format |
|---|---|---|
| LLM inference | `chat` / `generate_content` | `chat {model}` |
| Tool execution | `execute_tool` | `execute_tool {tool_name}` |
| Agent invocation | `invoke_agent` | `invoke_agent {agent_name}` |

**Standard attributes for `execute_tool`:**
- Required: `gen_ai.operation.name`, `gen_ai.tool.name`
- Optional: `gen_ai.tool.call.id`, `gen_ai.tool.description`

**Custom causal attributes to add:**
- `tool.input_hash` — SHA256 of serialized input (enables deduplication and diff)
- `tool.result_category` — `success` / `partial` / `error` / `timeout` / `blocked`
- `gen_ai.harness.candidate_id` — links span to optimization candidate
- `gen_ai.harness.iteration` — iteration number in the search loop

**No dedicated `OpenTelemetry.Instrumentation.AI` package exists.** Use `System.Diagnostics.ActivitySource` directly. Semantic Kernel emits via `Microsoft.SemanticKernel*` source.

```csharp
private static readonly ActivitySource _source = new("AgenticHarness.Tools");

using var activity = _source.StartActivity($"execute_tool {toolName}", ActivityKind.Internal);
activity?.SetTag("gen_ai.operation.name", "execute_tool");
activity?.SetTag("gen_ai.tool.name", toolName);
activity?.SetTag("tool.input_hash", ComputeHash(input));
activity?.SetTag("tool.result_category", CategorizeResult(result));
```

Guard expensive tag computation with `activity?.IsAllDataRequested`.

Sources: OTel GenAI semconv, .NET distributed tracing docs, Semantic Kernel observability docs

### Topic 3: Harness Search Loop Algorithms

**Meta-Harness outer loop design** (single-proposer, full-history, filesystem-based):

```
Loop for iteration i in 1..maxIterations:
  1. Proposer agent reads filesystem D (all prior code, traces, scores via grep/cat)
  2. Proposer diagnoses failure modes from execution traces
  3. Proposer generates new harness (local edit or full rewrite)
  4. Evaluate new harness on search set (50-100 examples)
  5. Store {code, scores, traces} in new directory → D grows
  6. Repeat
```

**No fixed population** — the proposer decides what to read from the filesystem. The filesystem IS the population; selective attention manages the effective window.

**Context per iteration comparison:**
| Method | History type | MTok/iter |
|---|---|---|
| OPRO | Window of (solution, score) pairs | 0.002 |
| TextGrad | Last iteration only | 0.015 |
| AlphaEvolve | Program DB + scores | 0.022 |
| Meta-Harness | Full filesystem | ~10.0 |

**Practical parameters:**
- 3–5 short runs (3–5 iters each) to debug skill text before a full run
- Search set: 50–100 examples (fast discriminative eval beats large eval)
- Scoring: pass/fail rate + context token count → optimize Pareto frontier of accuracy vs. token efficiency
- Candidate selection: best score on search set (not held-out test set)

**MediatR CQRS pattern for the loop:**
```csharp
public record ProposeHarnessCommand(Guid RunId, int Iteration) : IRequest<HarnessCandidateResult>;
public record EvaluateHarnessCommand(Guid CandidateId, string HarnessCode) : IRequest<EvaluationResult>;
// RunHarnessOptimizationCommandHandler loops over iterations, calling both
```

### Topic 4: Non-Markovian Agent Memory

**MemGPT / Letta model**: Treats LLM context like OS virtual memory:
- **Main context (in-window)**: Active scratchpad + recent messages
- **Archival storage (external)**: Persistent, append-only, searchable
- Control via function calls: agent explicitly calls `archive_memory()` or `search_archival()` tools

**JSONL append-only event log pattern:**
```jsonl
{"seq":1,"ts":"2026-04-10T12:00:00Z","type":"tool_call","tool":"file_read","input":{"path":"/foo.txt"},"turn_id":"abc","run_id":"xyz"}
{"seq":2,"ts":"2026-04-10T12:00:01Z","type":"tool_result","tool":"file_read","result_category":"success","latency_ms":42,"turn_id":"abc","run_id":"xyz"}
{"seq":3,"ts":"2026-04-10T12:00:02Z","type":"decision","reasoning":"File shows X, therefore Y","action":"invoke_tool","tool":"bash","turn_id":"abc","run_id":"xyz"}
```

**Abstraction comparison:**
| Approach | Best for | Tradeoffs |
|---|---|---|
| Append-only JSONL | File-based, grep-friendly, optimization | No joins, no transactions |
| Event sourcing (Marten) | Audit trails, replay, projection | Infrastructure overhead |
| SQLite | Queryable history, lightweight | Schema migration burden |
| Postgres | Multi-agent, production scale | Overkill for single-agent |

**Recommendation**: JSONL per-run with lazy SQLite FTS index for cross-run queries. Matches Meta-Harness filesystem pattern; proposer can use `grep`.

**C# abstraction:**
```csharp
public sealed record AgentDecisionEvent
{
    public required long Sequence { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required string EventType { get; init; }  // tool_call, tool_result, decision, observation
    public required string RunId { get; init; }
    public required string TurnId { get; init; }
    public string? ToolName { get; init; }
    public string? ResultCategory { get; init; }
    public JsonElement? Payload { get; init; }
}

public interface IDecisionLog
{
    ValueTask AppendAsync(AgentDecisionEvent evt, CancellationToken ct = default);
    IAsyncEnumerable<AgentDecisionEvent> QueryAsync(DecisionLogQuery query, CancellationToken ct = default);
}
```

`DecisionLogQuery` takes `runId`, `turnId`, `eventType`, `toolName`, `since` (sequence) — enables retrieving only decisions after a checkpoint.

Sources: MemGPT arXiv:2310.08560, Letta GitHub, Meta-Harness paper (arXiv:2603.28052)

---

## Testing Setup

**Existing framework**: xUnit + Moq. Test projects mirror source namespaces.

**Existing test projects (relevant)**:
- `Application.AI.Common.Tests` — unit tests for AI abstractions
- `Application.Core.Tests` — CQRS handler tests
- `Infrastructure.AI.Tests` — tool/service integration tests

**Pattern for new tests**:
- Unit tests for domain models and interfaces in `Application.AI.Common.Tests`
- Command handler tests in `Application.Core.Tests`  
- FileSystem trace store tests in `Infrastructure.AI.Tests`
- Tests should use `IOptionsMonitor<T>` with `Options.Create<T>()` for config
