# Section 9: Drift Baseline Store

## Status: IMPLEMENTED

## Overview

This section implements `IDriftBaselineStore` — the persistence layer for drift detection baselines. Two implementations are provided:

1. **`GraphDriftBaselineStore`** — production implementation backed by `IKnowledgeGraphStore`, using deterministic node IDs for O(1) lookups.
2. **`InMemoryDriftBaselineStore`** — testing implementation using `ConcurrentDictionary` with case-normalized keys.

Both implementations live in `Infrastructure.AI` (not `Infrastructure.AI.KnowledgeGraph`) because the baseline store is a drift detection concern that happens to use the knowledge graph as storage, following the same pattern as other drift detection infrastructure components.

## Implementation Notes

- Tests use Moq mocks (not InMemoryGraphStore) for consistency with `GraphEwmaStateStoreTests`.
- `BuildId` includes colon/null/whitespace guards matching `GraphEwmaStateStore.BuildId`.
- `InMemoryDriftBaselineStore` normalizes keys to lowercase for behavioral parity with the graph store.
- `SaveBaselineAsync` performs 3 sequential writes (node, scope node, edge) — non-transactional by design; consumers needing atomicity should wrap in a graph-backend transaction.
- 15 tests total: 10 for graph store (including 3 error-path tests), 5 for in-memory store.

## Dependencies

- **Section 1 (Drift Domain Models):** `DriftBaseline`, `DriftScope` records and enums from `Domain.AI/DriftDetection/`.
- **Section 5 (Drift Interfaces):** `IDriftBaselineStore` interface from `Application.AI.Common/Interfaces/DriftDetection/`.
- **Existing infrastructure:** `IKnowledgeGraphStore`, `GraphNode`, `GraphEdge` from the knowledge graph layer.

## File Paths

### Production Code

| File | Project | Purpose |
|------|---------|---------|
| `src/Content/Infrastructure/Infrastructure.AI/DriftDetection/GraphDriftBaselineStore.cs` | Infrastructure.AI | Graph-backed baseline store |
| `src/Content/Infrastructure/Infrastructure.AI/DriftDetection/InMemoryDriftBaselineStore.cs` | Infrastructure.AI | In-memory testing store |

### Test Code

| File | Project | Purpose |
|------|---------|---------|
| `src/Content/Tests/Infrastructure.AI.Tests/DriftDetection/GraphDriftBaselineStoreTests.cs` | Infrastructure.AI.Tests | Tests for graph store |
| `src/Content/Tests/Infrastructure.AI.Tests/DriftDetection/InMemoryDriftBaselineStoreTests.cs` | Infrastructure.AI.Tests | Tests for in-memory store |

---

## Tests (Write First)

Tests should use the real `InMemoryGraphStore` (from `Infrastructure.AI.KnowledgeGraph.InMemory`) as the backing store for `GraphDriftBaselineStore`, following the same pattern used by `GraphSkillEffectivenessTrackerTests`. This avoids brittle mock setups and tests actual serialization round-tripping.

### GraphDriftBaselineStoreTests

```csharp
// File: src/Content/Tests/Infrastructure.AI.Tests/DriftDetection/GraphDriftBaselineStoreTests.cs
// Namespace: Infrastructure.AI.Tests.DriftDetection

// Setup: create InMemoryGraphStore, inject into GraphDriftBaselineStore

// Test: SaveBaseline_Graph_CreatesNodeWithDeterministicId
//   Arrange: build a DriftBaseline with Scope=Skill, ScopeIdentifier="code_review"
//   Act: call SaveBaselineAsync
//   Assert: underlying graph store contains node with ID "driftbaseline:skill:code_review"
//           node.Type == "DriftBaseline"
//           node.Properties contains serialized baseline data (Dimensions, DimensionSigmas, SampleCount, etc.)

// Test: GetBaseline_Graph_RetrievesByDeterministicId
//   Arrange: save a baseline for Scope=Agent, ScopeIdentifier="agent-1"
//   Act: call GetBaselineAsync(DriftScope.Agent, "agent-1")
//   Assert: returns Result with the saved baseline, all fields match original

// Test: GetBaseline_NotFound_ReturnsNull
//   Arrange: empty store
//   Act: call GetBaselineAsync(DriftScope.Skill, "nonexistent")
//   Assert: returns Result<DriftBaseline?>.Success(null)

// Test: SaveBaseline_OverwritesExistingBaseline
//   Arrange: save baseline v1 for Scope=TaskType, ScopeIdentifier="summarization"
//   Act: save baseline v2 for same scope+identifier (different dimensions/sample count)
//   Assert: GetBaselineAsync returns v2, not v1

// Test: GetBaselines_ByScope_ReturnsAll
//   Arrange: save 3 baselines — 2 with Scope=Skill (different identifiers), 1 with Scope=Agent
//   Act: call GetBaselinesAsync(DriftScope.Skill)
//   Assert: returns exactly the 2 Skill-scoped baselines

// Test: GetBaselines_NullScope_ReturnsAll
//   Arrange: save baselines across multiple scopes
//   Act: call GetBaselinesAsync(null)
//   Assert: returns all saved baselines

// Test: SaveBaseline_CreatesBaselineForEdge
//   Arrange: save a baseline
//   Act: check graph store for edge with predicate "baseline_for"
//   Assert: edge exists from baseline node to a scope identifier node
```

### InMemoryDriftBaselineStoreTests

```csharp
// File: src/Content/Tests/Infrastructure.AI.Tests/DriftDetection/InMemoryDriftBaselineStoreTests.cs
// Namespace: Infrastructure.AI.Tests.DriftDetection

// Test: InMemory_SaveAndRetrieve_RoundTrips
//   Arrange: create baseline with all fields populated
//   Act: SaveBaselineAsync, then GetBaselineAsync with same scope+identifier
//   Assert: retrieved baseline equals saved baseline (record equality)

// Test: InMemory_OverwriteExisting_ReplacesValue
//   Arrange: save baseline v1
//   Act: save baseline v2 with same scope+identifier
//   Assert: GetBaselineAsync returns v2

// Test: InMemory_GetBaselines_FiltersByScope
//   Arrange: save mix of scopes
//   Act: GetBaselinesAsync with specific scope
//   Assert: only matching scope returned

// Test: InMemory_GetBaseline_NotFound_ReturnsNull
//   Act: GetBaselineAsync for nonexistent key
//   Assert: Result.Success with null value
```

---

## Implementation Details

### Deterministic Node ID Convention

All baselines use the pattern `"driftbaseline:{scope}:{identifier}"` where scope is lowercased:
- `"driftbaseline:agent:agent-1"`
- `"driftbaseline:skill:code_review"`
- `"driftbaseline:tasktype:summarization"`

This enables O(1) lookup via `IKnowledgeGraphStore.GetNodeAsync(nodeId)` — no full-scan queries needed.

### GraphDriftBaselineStore

Implements `IDriftBaselineStore`. Injects `IKnowledgeGraphStore` and `ILogger<GraphDriftBaselineStore>`.

**`SaveBaselineAsync` flow:**
1. Build deterministic node ID: `$"driftbaseline:{scope.ToString().ToLowerInvariant()}:{identifier.ToLowerInvariant()}"`
2. Serialize the `DriftBaseline` properties into the `GraphNode.Properties` dictionary (all values as strings, using `System.Text.Json.JsonSerializer` for complex types like `Dimensions` and `DimensionSigmas` dictionaries)
3. Create node with `Type = "DriftBaseline"` and `Name` describing the scope
4. Call `IKnowledgeGraphStore.AddNodesAsync` — the graph store's idempotent upsert handles overwrite
5. Create a scope identifier node if it does not exist (e.g., `"scope:skill:code_review"`) and an edge with predicate `"baseline_for"` from baseline node to scope node
6. Return `Result.Success()`

**`GetBaselineAsync` flow:**
1. Build deterministic node ID from scope + identifier
2. Call `IKnowledgeGraphStore.GetNodeAsync(nodeId)`
3. If null, return `Result<DriftBaseline?>.Success(null)`
4. Deserialize `GraphNode.Properties` back into a `DriftBaseline` record
5. Return `Result<DriftBaseline?>.Success(baseline)`

**`GetBaselinesAsync` flow:**
1. Call `IKnowledgeGraphStore.GetAllNodesAsync()` — filter by `Type == "DriftBaseline"`
2. If scope filter is provided, additionally filter by the `Scope` property stored in node properties
3. Deserialize each matching node into a `DriftBaseline`
4. Return the list

**Serialization strategy for `Properties` dictionary:**
- `BaselineId` -> `baseline.BaselineId.ToString()`
- `Scope` -> `baseline.Scope.ToString()`
- `ScopeIdentifier` -> `baseline.ScopeIdentifier`
- `Dimensions` -> `JsonSerializer.Serialize(baseline.Dimensions)` (dictionary of DriftDimension -> double)
- `DimensionSigmas` -> `JsonSerializer.Serialize(baseline.DimensionSigmas)`
- `SampleCount` -> `baseline.SampleCount.ToString()`
- `WindowStart` -> `baseline.WindowStart.ToString("O")`
- `WindowEnd` -> `baseline.WindowEnd.ToString("O")`
- `CreatedAt` -> `baseline.CreatedAt.ToString("O")`

This follows the same pattern as `GraphSkillEffectivenessTracker` which stores metrics in `Properties` as strings.

### InMemoryDriftBaselineStore

Simple `ConcurrentDictionary<(DriftScope, string), DriftBaseline>` backed store. All methods are synchronous wrapped in `Task.FromResult`. Thread-safe via `ConcurrentDictionary`.

**Key lookup:** Tuple of `(DriftScope, string scopeIdentifier)` — natural composite key.

**`GetBaselinesAsync`:** Enumerate all values, optionally filter by scope if the scope parameter is not null.

**Return types:** All methods return `Result.Success()` / `Result<T>.Success(value)` — this store cannot fail under normal conditions.

### DI Registration (handled in Section 18)

Both stores will be registered with keyed DI:
- `IDriftBaselineStore [keyed "graph"]` -> `GraphDriftBaselineStore` (Singleton)
- `IDriftBaselineStore [keyed "in_memory"]` -> `InMemoryDriftBaselineStore` (Singleton)
- Default `IDriftBaselineStore` resolved from config (e.g., `DriftDetection.StoreProvider` or defaulting to `"graph"`)

### Error Handling

- `GraphDriftBaselineStore` wraps graph store calls in try/catch, returning `Result.Fail(...)` with descriptive messages for serialization errors or graph store failures.
- `InMemoryDriftBaselineStore` does not need error handling — `ConcurrentDictionary` operations are infallible for these use cases.

### XML Documentation

Both classes require full XML documentation on all public members. This is a template project — docs are teaching material. Follow the same style as `GraphFeedbackStore` and `GraphSkillEffectivenessTracker`.
