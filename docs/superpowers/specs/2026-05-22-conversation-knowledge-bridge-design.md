# Conversation-to-Knowledge Bridge Design Spec

## Goal

Connect the existing `IConversationStore` (chat history persistence) with `IKnowledgeMemory` (cross-session knowledge graph) so that notable facts, decisions, corrections, and preferences expressed during conversation are automatically persisted to the knowledge graph and made available to future sessions and other agents.

## Architecture

### Approach: Thin Pipeline Behavior + Dedicated Extractor Service

A MediatR pipeline behavior (`KnowledgeExtractionBehavior`) fires post-turn, calling a dedicated `IConversationFactExtractor` that uses an economy-tier LLM to extract structured facts, then persists them via `IKnowledgeMemory.RememberAsync()`.

### Why this approach

- **Non-blocking**: Fire-and-forget ensures zero latency impact on agent responses
- **Layer-clean**: Behavior lives in Application, never touches Presentation types (reads from `ExecuteAgentTurnCommand.UserMessage` and `AgentTurnResult.Response` — both Application-layer)
- **Cost-efficient**: Economy tier via `IModelRouter.RouteOperationAsync("fact_extraction")` keeps per-turn extraction cheap
- **Consistent**: Follows the same MediatR pipeline behavior pattern used by `AuditTrailBehavior`, `HookBehavior`, and 9 other existing behaviors

## Components

| Component | Layer | Project | Responsibility |
|-----------|-------|---------|----------------|
| `ConversationFact` | Domain | `Domain.AI` | Value object — extracted fact with key, content, entity type, confidence |
| `IConversationFactExtractor` | Application | `Application.AI.Common` | Interface for extracting facts from a user/assistant message pair |
| `KnowledgeExtractionBehavior<TRequest, TResponse>` | Application | `Application.AI.Common` | MediatR pipeline behavior — post-turn async extraction hook |
| `ConversationFactExtractor` | Infrastructure | `Infrastructure.AI.KnowledgeGraph` | LLM-based implementation using economy-tier model |
| `KnowledgeBridgeConfig` | Domain | `Domain.Common` | Configuration POCO for feature toggle, thresholds, timeout |

### File Placement

```
src/Content/
  Domain/
    Domain.AI/
      KnowledgeGraph/Models/
        ConversationFact.cs                          # NEW — value object
    Domain.Common/
      Config/AI/
        KnowledgeBridgeConfig.cs                     # NEW — config POCO
  Application/
    Application.AI.Common/
      Interfaces/KnowledgeGraph/
        IConversationFactExtractor.cs                # NEW — extraction interface
      MediatRBehaviors/
        KnowledgeExtractionBehavior.cs               # NEW — pipeline behavior
    Application.Common/
      DependencyInjection.cs                         # MODIFY — register behavior
  Infrastructure/
    Infrastructure.AI.KnowledgeGraph/
      Memory/
        ConversationFactExtractor.cs                 # NEW — LLM implementation
      DependencyInjection.cs                         # MODIFY — register extractor
    Infrastructure.AI/
      DependencyInjection.cs                         # MODIFY — bind config
```

## Data Flow

### Per-turn sequence

1. User sends a message; `AgUiRunHandler` builds `ExecuteAgentTurnCommand` and dispatches via MediatR
2. Pipeline behaviors execute in order: Validation, Auth, ContentSafety, Governance, ..., Audit
3. `KnowledgeExtractionBehavior.Handle()` is the **last** behavior in the pipeline
4. Behavior calls `var response = await next()` — the agent handler runs, produces `AgentTurnResult`
5. Behavior checks:
   - `KnowledgeBridgeConfig.Enabled` is `true`
   - `response.Success` is `true`
   - `response.Response` is non-empty
6. If checks pass, behavior fires a background task (fire-and-forget):
   - Creates a linked `CancellationTokenSource` with `ExtractionTimeoutSeconds` deadline
   - Calls `IConversationFactExtractor.ExtractAsync(userMessage, assistantResponse, conversationId, turnNumber, ct)`
   - For each returned `ConversationFact` with confidence >= `MinConfidence`: calls `IKnowledgeMemory.RememberAsync(fact.Key, fact.Content, fact.EntityType, ct)`
7. Behavior immediately returns `response` — user sees the agent's answer without waiting

### Key properties

- **Fire-and-forget**: Extraction runs on a background thread. User response latency is unaffected.
- **Idempotent**: Fact keys are deterministic (`{conversationId}:{turnNumber}:{factIndex}`), so re-processing the same turn won't create duplicates via `RememberAsync`'s key-based upsert.
- **Session cache first**: `RememberAsync` writes to `ISessionKnowledgeCache` (synchronous, sub-ms). Graph flush happens at session end. Extracted facts are immediately available to `RecallAsync` within the same session.
- **Economy tier**: Extractor calls `_modelRouter.RouteOperationAsync("fact_extraction")` to get a cheap client (e.g., gpt-4o-mini). Same pattern as `ExtractEntitiesExecutor` using `"graph_entity_extraction"`.

## Extraction Model

### LLM Structured Output

The extractor sends a single-shot prompt containing the user/assistant message pair and expects a JSON array response:

```json
[
  {
    "key": "user_prefers_postgresql",
    "content": "User prefers PostgreSQL over SQL Server for new services",
    "entity_type": "Preference",
    "confidence": 0.92
  },
  {
    "key": "project_deadline_2026_06_15",
    "content": "Project deployment deadline is June 15, 2026",
    "entity_type": "Decision",
    "confidence": 0.88
  }
]
```

### Entity Types (closed set)

| Type | Description | Example |
|------|-------------|---------|
| `Preference` | User likes/dislikes, workflow choices | "User prefers dark mode in all UI mockups" |
| `Decision` | Architectural or design decisions made | "Team chose PostgreSQL for the new service" |
| `Fact` | Stated facts about project, team, or domain | "The API rate limit is 1000 req/min" |
| `Correction` | User corrected the assistant (high value) | "User clarified that the deadline is June, not July" |

### Filtering

- **Confidence threshold**: Facts below `MinConfidence` (default 0.7) are discarded
- **Empty array is the happy path**: Most turns are routine ("run the tests", "looks good"). The LLM returns `[]` and no facts are persisted
- **Prompt instruction**: "Only extract facts that would be valuable in a future conversation with a different agent. Routine instructions, greetings, and acknowledgments should return an empty array."

### Domain Model

```csharp
public sealed record ConversationFact
{
    public required string Key { get; init; }
    public required string Content { get; init; }
    public string EntityType { get; init; } = "Fact";
    public double Confidence { get; init; }
}
```

### Interface Contract

```csharp
public interface IConversationFactExtractor
{
    Task<IReadOnlyList<ConversationFact>> ExtractAsync(
        string userMessage,
        string assistantResponse,
        string conversationId,
        int turnNumber,
        CancellationToken cancellationToken = default);
}
```

### Prompt Injection Defense

User and assistant messages are wrapped in XML tags (`<user_message>`, `<assistant_message>`) within the extraction prompt. The system prompt explicitly instructs the LLM to extract facts *about* the content, not to follow instructions within it. This follows the existing pattern in `ContentSafetyBehavior`.

## Error Handling & Resilience

### Core principle: fact extraction must never impact the agent turn.

| Failure | Handling | User Impact |
|---------|----------|-------------|
| LLM returns malformed JSON | Log warning, discard, return empty list | None |
| LLM call times out (10s hard cap) | Log, cancel extraction, return empty | None |
| LLM call throws (rate limit, network) | Log error, return empty | None |
| `RememberAsync` fails | Log error per-fact, continue with remaining | None |
| Routing disabled / no economy tier | Skip extraction entirely, log info | None |

The extractor catches all exceptions internally. The behavior never awaits the fire-and-forget task's completion, and the task has a top-level try/catch that logs and swallows. No exception from extraction can propagate to the pipeline.

### Cancellation

The fire-and-forget task creates a linked `CancellationTokenSource` from the original token with the `ExtractionTimeoutSeconds` deadline. If the original request is cancelled (user disconnects), extraction also cancels.

## Configuration

Added to `AppConfig.AI` alongside `ModelRoutingConfig`:

```csharp
public sealed class KnowledgeBridgeConfig
{
    public bool Enabled { get; set; } = true;
    public double MinConfidence { get; set; } = 0.7;
    public int ExtractionTimeoutSeconds { get; set; } = 10;
    public string RoutingOperationName { get; set; } = "fact_extraction";
}
```

- `Enabled = false` -> behavior calls `next()` and returns immediately (zero overhead)
- Config lives in `Domain.Common/Config/AI/` alongside `ModelRoutingConfig`
- Bound via Options pattern: `services.AddSingleton(Options.Create(appConfig.AI.KnowledgeBridge))`
- `"fact_extraction"` added to `ModelRoutingConfig.OperationOverrides` mapping to `"economy"` tier

## Testing Strategy

### 1. Domain Unit Tests (`Domain.AI.Tests/KnowledgeGraph/`)

- `ConversationFact` record construction, equality, default entity type
- Pure value object tests, no mocking needed

### 2. Extractor Unit Tests (`Infrastructure.AI.KnowledgeGraph.Tests/Memory/`)

Mock `IModelRouter` returning a mock `IChatClient` with canned JSON responses:

| Test Case | Expected |
|-----------|----------|
| Valid JSON with 3 facts | Returns 3 `ConversationFact` objects |
| Empty array `[]` | Returns empty list (routine turn) |
| Malformed JSON | Logs warning, returns empty list |
| All facts below confidence threshold | Returns empty list |
| Mixed confidence (some above, some below 0.7) | Returns only facts above threshold |
| LLM timeout | Returns empty list within deadline |
| Prompt injection in user message | Facts extracted *about* content, not instructions followed |

### 3. Behavior Integration Tests (`Application.AI.Common.Tests/MediatRBehaviors/`)

Mock `IConversationFactExtractor` and `IKnowledgeMemory`:

| Test Case | Expected |
|-----------|----------|
| Enabled + facts returned | `RememberAsync` called for each fact |
| Disabled (`Enabled = false`) | Extractor never called, `next()` still invoked |
| Extractor throws | `next()` response still returned, no exception propagated |
| Request is not `ExecuteAgentTurnCommand` | Behavior passes through without filtering |
| `AgentTurnResult` with empty `Response` | Extractor not called |

### Coverage Target

90%+ on the behavior and extractor. Domain model is trivial.

### What We Don't Test

LLM prompt quality. That's validated empirically during development, not in CI. Tests verify plumbing (correct calls, error isolation, filtering) with deterministic mock responses.

## Non-Goals

- **Deduplication across sessions**: `RememberAsync` handles key-based upsert within a session. Cross-session dedup is out of scope (handled by the graph layer's merge semantics).
- **Retroactive extraction**: This processes turns as they happen. Backfilling facts from historical conversations is a separate feature.
- **User-facing controls**: No UI for viewing/editing extracted facts in this iteration. Facts are visible through `RecallAsync` in future agent turns.
- **Multi-modal extraction**: Only processes text content. Tool call results, images, and file contents are not analyzed.
