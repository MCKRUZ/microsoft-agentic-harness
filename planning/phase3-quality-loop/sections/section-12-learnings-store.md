# Section 12: Learnings Store (Graph-Backed)

## Status: IMPLEMENTED

## Overview

This section implements two `ILearningsStore` implementations: `GraphLearningsStore` (production, graph-backed) and `InMemoryLearningsStore` (testing). The graph store uses the knowledge graph with deterministic node IDs and synthetic index nodes for efficient scope-hierarchy search, following the same pattern established by `GraphSkillEffectivenessTracker`.

**Layer:** Infrastructure.AI.KnowledgeGraph
**Depends on:** Section 02 (LearningEntry, LearningScope, LearningCategory, DecayClass domain models), Section 06 (ILearningsStore interface, LearningSearchCriteria DTO)
**Blocks:** Section 13 (MediatR command handlers), Section 18 (DI registration)

## Deviations from Plan
1. **All graph operations wrapped in try/catch** — Returns `Result.Fail` on exceptions (matches `GraphDriftBaselineStore` pattern, not in original plan).
2. **`UpdateAsync` checks existence** — Returns `Result.Fail("Learning not found")` for nonexistent IDs instead of silently upserting. Matches `InMemoryLearningsStore` contract.
3. **`SoftDeleteAsync` uses `.ToDictionary()`** — Instead of `new Dictionary(existing.Properties)` for `IReadOnlyDictionary` safety.
4. **Dead code removed from `InMemoryLearningsStore.MatchesScope`** — Unreachable `criteriaScope.IsGlobal && entryScope.IsGlobal` branch (entryScope.IsGlobal already handled above).
5. **28 tests** — 24 from original spec + 4 review-driven additions (Update_NotFound, round-trip fidelity, MinFeedbackWeight filter, CreatedAfter/CreatedBefore filter).

**Dependencies (must be implemented first):**
- Section 02 — LearningEntry, LearningScope, LearningCategory, DecayClass domain models
- Section 06 — ILearningsStore interface, LearningSearchCriteria DTO

---

## Background

### Graph Storage Model

Learnings are stored as knowledge graph nodes using `IKnowledgeGraphStore`. The store follows the same synthetic index node pattern as `GraphSkillEffectivenessTracker` (file: `src/Content/Infrastructure/Infrastructure.AI.KnowledgeGraph/Skills/GraphSkillEffectivenessTracker.cs`).

Key design decisions:

- **Deterministic node IDs** for O(1) lookup: `"learning:{learningId}"` enables `GetNodeAsync` by ID without scanning.
- **Synthetic index nodes** for scope-based search: Instead of scanning all nodes, index nodes aggregate learnings per scope level. The `GetNeighborsAsync` call on an index node returns all learnings for that scope.
  - `"learningindex:agent:{agentId}"` -- all learnings for a specific agent
  - `"learningindex:team:{teamId}"` -- all learnings for a team
  - `"learningindex:global"` -- all global learnings
- **Soft delete** via `Properties["IsDeleted"]` flag rather than physical deletion (preserves audit trail).
- Learning data serialized into `GraphNode.Properties` as string key-value pairs (same pattern as `GraphSkillEffectivenessTracker` and `GraphFeedbackStore`).

### Scope Hierarchy Search

When searching for learnings, the store must merge results from multiple scope levels:
1. Agent-specific learnings (if `AgentId` provided)
2. Team-level learnings (if `TeamId` provided)
3. Global learnings (always included)

Results are deduplicated by `LearningId` and filtered for soft-deleted entries.

---

## Dependencies from Prior Sections

**From Section 02 (Domain):** `LearningEntry`, `LearningScope`, `LearningCategory`, `DecayClass`, `LearningSourceType`, `LearningSource`, `LearningProvenance`, `WeightedLearning`
**From Section 06 (Interfaces):** `ILearningsStore` (with `SaveAsync`, `GetAsync`, `SearchAsync`, `UpdateAsync`, `SoftDeleteAsync`), `LearningSearchCriteria`
**Existing codebase:** `IKnowledgeGraphStore`, `GraphNode`, `GraphEdge`, `InMemoryGraphStore`, `Result<T>`, `Result`

---

## Files to Create

| File | Action |
|------|--------|
| `src/Content/Tests/Infrastructure.AI.KnowledgeGraph.Tests/Learnings/GraphLearningsStoreTests.cs` | Create |
| `src/Content/Tests/Infrastructure.AI.KnowledgeGraph.Tests/Learnings/InMemoryLearningsStoreTests.cs` | Create |
| `src/Content/Infrastructure/Infrastructure.AI.KnowledgeGraph/Learnings/GraphLearningsStore.cs` | Create |
| `src/Content/Infrastructure/Infrastructure.AI.KnowledgeGraph/Learnings/InMemoryLearningsStore.cs` | Create |

---

## Tests (Write First)

### GraphLearningsStoreTests.cs

**Project:** `src/Content/Tests/Infrastructure.AI.KnowledgeGraph.Tests/Learnings/GraphLearningsStoreTests.cs`
**Namespace:** `Infrastructure.AI.KnowledgeGraph.Tests.Learnings`

Test setup: Create an `InMemoryGraphStore` (from `Infrastructure.AI.KnowledgeGraph.InMemory`) as the backing store, and pass it to `GraphLearningsStore`. This gives integration-level coverage without mocks.

```csharp
// Setup: InMemoryGraphStore + GraphLearningsStore, ILogger mocks
// Helper: BuildLearningEntry(Guid? id, string? agentId, string? teamId, bool isGlobal, LearningCategory category)

// --- Save Tests ---

// Test: Save_Graph_CreatesNodeWithDeterministicId
//   Save a LearningEntry with known GUID
//   Verify GetNodeAsync("learning:{guid}") returns non-null
//   Verify node Type == "LearningEntry"
//   Verify node Properties contain serialized LearningEntry fields (Content, Category, DecayClass, FeedbackWeight, etc.)

// Test: Save_Graph_CreatesIndexEdges_AgentScope
//   Save a LearningEntry with AgentId="agent-1", TeamId=null, IsGlobal=false
//   Verify index node "learningindex:agent:agent-1" exists
//   Verify edge from index node to learning node with predicate "has_learning"

// Test: Save_Graph_CreatesIndexEdges_TeamScope
//   Save a LearningEntry with TeamId="team-1"
//   Verify index node "learningindex:team:team-1" exists
//   Verify edge with predicate "has_learning"

// Test: Save_Graph_CreatesIndexEdges_GlobalScope
//   Save a LearningEntry with IsGlobal=true
//   Verify index node "learningindex:global" exists
//   Verify edge with predicate "has_learning"

// Test: Save_Graph_CreatesMultipleIndexEdges
//   Save a LearningEntry with AgentId="a1", TeamId="t1", IsGlobal=true
//   Verify all three index nodes exist with edges to the learning node

// --- Get Tests ---

// Test: Get_Graph_RetrievesByDeterministicId
//   Save entry, then GetAsync with the same GUID
//   Verify returned LearningEntry matches original (Content, Category, Scope, FeedbackWeight)

// Test: Get_NotFound_ReturnsNull
//   GetAsync with unknown GUID
//   Verify result IsSuccess and Value is null

// --- Search Tests ---

// Test: Search_AgentScope_ReturnsAgentLearnings
//   Save 3 entries: 2 for agent-1, 1 for agent-2
//   Search with criteria AgentId="agent-1"
//   Verify exactly 2 results returned

// Test: Search_TeamScope_ReturnsTeamLearnings
//   Save entries for different teams
//   Search with criteria TeamId="team-1"
//   Verify only team-1 learnings returned

// Test: Search_GlobalScope_ReturnsGlobalLearnings
//   Save 2 global entries and 1 agent-scoped entry
//   Search with criteria IsGlobal=true
//   Verify only global learnings returned

// Test: Search_ScopeHierarchy_MergesAllLevels
//   Save: 1 agent-specific, 1 team-level, 1 global learning
//   Search with criteria AgentId="a1", TeamId="t1" (hierarchy query)
//   Verify all 3 returned (merged from agent + team + global index nodes)

// Test: Search_DeduplicatesByLearningId
//   Save a learning with AgentId="a1", TeamId="t1", IsGlobal=true (appears in 3 indexes)
//   Search with all scope levels
//   Verify only 1 result (not 3 duplicates)

// Test: Search_ExcludesSoftDeleted
//   Save 2 entries, soft-delete one
//   Search -> verify only non-deleted entry returned

// Test: Search_FiltersByCategory
//   Save entries with different categories
//   Search with Category=FactualCorrection
//   Verify only matching category returned

// --- Soft Delete Tests ---

// Test: SoftDelete_SetsIsDeletedFlag
//   Save entry, soft-delete it
//   Verify graph node Properties["IsDeleted"] == "true"

// Test: SoftDelete_SetsDeleteReason
//   Soft-delete with reason "outdated"
//   Verify graph node Properties["DeleteReason"] == "outdated"

// --- Update Tests ---

// Test: Update_PreservesGraphNodeId
//   Save entry, update FeedbackWeight
//   Verify same deterministic node ID still exists
//   Verify updated properties reflected
```

### InMemoryLearningsStoreTests.cs

**Project:** `src/Content/Tests/Infrastructure.AI.KnowledgeGraph.Tests/Learnings/InMemoryLearningsStoreTests.cs`
**Namespace:** `Infrastructure.AI.KnowledgeGraph.Tests.Learnings`

```csharp
// Test: InMemory_SaveAndRetrieve_RoundTrips
//   Save a LearningEntry, GetAsync by ID
//   Verify all fields match

// Test: InMemory_ScopeHierarchySearch_Works
//   Save entries at different scope levels (agent, team, global)
//   Search with all scope levels -> verify merged results
//   Search with only agent scope -> verify only agent + global results
```

---

## Implementation Details

### GraphLearningsStore

**File:** `src/Content/Infrastructure/Infrastructure.AI.KnowledgeGraph/Learnings/GraphLearningsStore.cs`
**Namespace:** `Infrastructure.AI.KnowledgeGraph.Learnings`

Constructor dependencies:
- `IKnowledgeGraphStore graphStore`
- `ILogger<GraphLearningsStore> logger`

Implements `ILearningsStore`. Registered with keyed DI key `"graph"`.

#### Node ID Convention

```
Learning node:  "learning:{learningId}"          (GUID, lowered)
Agent index:    "learningindex:agent:{agentId}"   (lowered)
Team index:     "learningindex:team:{teamId}"     (lowered)
Global index:   "learningindex:global"
Edge IDs:       "edge:learningindex:{scope}:{id}:learning:{learningId}"
```

All IDs lowercased via `ToLowerInvariant()` (same convention as `GraphSkillEffectivenessTracker`).

#### SaveAsync

1. Serialize `LearningEntry` fields into `GraphNode.Properties` dictionary (all values as strings).
   - Properties to store: `Content`, `Category`, `DecayClass`, `FeedbackWeight`, `UpdateCount`, `CreatedAt`, `LastAccessedAt`, `LastReinforcedAt`, `Source.SourceType`, `Source.SourceId`, `Source.SourceDescription`, `Provenance.OriginPipeline`, `Provenance.OriginTask`, `Provenance.OriginTimestamp`, `Provenance.Confidence`, `Scope.AgentId`, `Scope.TeamId`, `Scope.IsGlobal`, `IsDeleted` (default: `"false"`)
2. Create `GraphNode` with:
   - `Id = $"learning:{learningId}".ToLowerInvariant()`
   - `Name = $"Learning: {entry.Content[..Math.Min(50, entry.Content.Length)]}"` (truncated for display)
   - `Type = "LearningEntry"`
3. Call `_graphStore.AddNodesAsync([node])` (idempotent upsert)
4. Create/ensure synthetic index nodes and edges for each applicable scope:
   - If `Scope.AgentId` is not null: create index node `"learningindex:agent:{agentId}"` (Type: `"LearningIndex"`, Name: `"Agent:{agentId}"`), add edge with predicate `"has_learning"`
   - If `Scope.TeamId` is not null: create index node `"learningindex:team:{teamId}"`, add edge
   - If `Scope.IsGlobal`: create index node `"learningindex:global"`, add edge
5. Return `Result.Success()`

#### GetAsync

1. Call `_graphStore.GetNodeAsync($"learning:{learningId}".ToLowerInvariant())`
2. If null, return `Result<LearningEntry?>.Success(null)`
3. Deserialize `GraphNode.Properties` back into a `LearningEntry` record
4. Return `Result<LearningEntry?>.Success(entry)`

#### SearchAsync

1. Collect candidate nodes from scope hierarchy:
   - If `criteria.AgentId` provided: `GetNeighborsAsync("learningindex:agent:{agentId}", maxDepth: 1)` and filter to `Type == "LearningEntry"`
   - If `criteria.TeamId` provided: `GetNeighborsAsync("learningindex:team:{teamId}", maxDepth: 1)` and filter
   - Always: `GetNeighborsAsync("learningindex:global", maxDepth: 1)` and filter
2. Merge all candidates, deduplicate by node ID (which contains the GUID)
3. Filter out soft-deleted nodes: skip where `Properties["IsDeleted"] == "true"`
4. Apply additional criteria filters:
   - If `criteria.Category` is set: filter `Properties["Category"]`
   - If `criteria.MinFeedbackWeight` is set: filter `double.Parse(Properties["FeedbackWeight"])`
   - If `criteria.CreatedAfter` is set: filter by `Properties["CreatedAt"]`
5. Deserialize remaining nodes into `LearningEntry` records
6. Return `Result<IReadOnlyList<LearningEntry>>.Success(entries)`

#### UpdateAsync

1. Serialize updated `LearningEntry` into properties (same as SaveAsync)
2. Create `GraphNode` with same deterministic ID (upsert overwrites)
3. Call `_graphStore.AddNodesAsync([node])` -- idempotent, preserves node ID
4. Return `Result.Success()`

Note: Index edges do not need updating on UpdateAsync because scope doesn't change after creation. If scope could change, the old edges would need removal -- but per the domain model, scope is set at creation time.

#### SoftDeleteAsync

1. Load existing node via `GetNodeAsync`
2. If not found, return `Result.Fail("Learning not found")`
3. Create new `GraphNode` with same ID, same properties but with:
   - `Properties["IsDeleted"] = "true"`
   - `Properties["DeleteReason"] = reason`
4. Call `_graphStore.AddNodesAsync([updatedNode])` (upsert overwrites properties)
5. Return `Result.Success()`

#### Deserialization Helper

Private method `DeserializeLearningEntry(GraphNode node) -> LearningEntry?` that reconstructs a `LearningEntry` from `GraphNode.Properties`. Handles missing/malformed properties gracefully with defaults and logging. Returns null if critical fields (Content, Category) are missing.

---

### InMemoryLearningsStore

**File:** `src/Content/Infrastructure/Infrastructure.AI.KnowledgeGraph/Learnings/InMemoryLearningsStore.cs`
**Namespace:** `Infrastructure.AI.KnowledgeGraph.Learnings`

Registered with keyed DI key `"in_memory"`. Simple implementation for testing.

#### Data Structure

`ConcurrentDictionary<Guid, LearningEntry>` for thread-safe in-memory storage. A second dictionary `ConcurrentDictionary<Guid, (bool IsDeleted, string Reason)>` tracks soft-delete state.

#### SaveAsync

Add/overwrite entry in the dictionary. Return `Result.Success()`.

#### GetAsync

Lookup by GUID. If not found or soft-deleted, return `Result<LearningEntry?>.Success(null)`. Otherwise return the entry.

#### SearchAsync

1. Filter all entries by scope hierarchy:
   - Include entries where `Scope.AgentId == criteria.AgentId` (if provided)
   - Include entries where `Scope.TeamId == criteria.TeamId` (if provided)
   - Include entries where `Scope.IsGlobal == true`
2. Exclude soft-deleted entries
3. Apply category and other filters from criteria
4. Return as `IReadOnlyList<LearningEntry>`

#### UpdateAsync

Overwrite the entry in the dictionary (same key = same GUID). Return `Result.Success()`.

#### SoftDeleteAsync

Mark as deleted in the soft-delete dictionary. Return `Result.Success()`. If not found, return `Result.Fail("Learning not found")`.

---

## Serialization Conventions

All `LearningEntry` fields are stored as `GraphNode.Properties` string values. Use these consistent property key names:

| Property Key | Source Field | Format |
|-------------|-------------|--------|
| `"Content"` | `LearningEntry.Content` | Raw string |
| `"Category"` | `LearningEntry.Category` | Enum name (e.g., `"FactualCorrection"`) |
| `"DecayClass"` | `LearningEntry.DecayClass` | Enum name |
| `"FeedbackWeight"` | `LearningEntry.FeedbackWeight` | `"F6"` format |
| `"UpdateCount"` | `LearningEntry.UpdateCount` | Integer string |
| `"CreatedAt"` | `LearningEntry.CreatedAt` | ISO 8601 round-trip (`"O"` format) |
| `"LastAccessedAt"` | `LearningEntry.LastAccessedAt` | ISO 8601 |
| `"LastReinforcedAt"` | `LearningEntry.LastReinforcedAt` | ISO 8601 |
| `"SourceType"` | `LearningEntry.Source.SourceType` | Enum name |
| `"SourceId"` | `LearningEntry.Source.SourceId` | Raw string |
| `"SourceDescription"` | `LearningEntry.Source.SourceDescription` | Raw string |
| `"ProvenancePipeline"` | `LearningEntry.Provenance.OriginPipeline` | Raw string |
| `"ProvenanceTask"` | `LearningEntry.Provenance.OriginTask` | Raw string |
| `"ProvenanceTimestamp"` | `LearningEntry.Provenance.OriginTimestamp` | ISO 8601 |
| `"ProvenanceConfidence"` | `LearningEntry.Provenance.Confidence` | `"F4"` format |
| `"ScopeAgentId"` | `LearningEntry.Scope.AgentId` | Raw string or empty |
| `"ScopeTeamId"` | `LearningEntry.Scope.TeamId` | Raw string or empty |
| `"ScopeIsGlobal"` | `LearningEntry.Scope.IsGlobal` | `"true"` / `"false"` |
| `"IsDeleted"` | Soft-delete flag | `"true"` / `"false"` |
| `"DeleteReason"` | Soft-delete reason | Raw string |

This matches the existing pattern in `GraphSkillEffectivenessTracker` where all properties are `Dictionary<string, string>`.

---

## Edge Cases and Error Handling

1. **Duplicate saves:** `AddNodesAsync` is idempotent (merges properties). Saving the same learning twice overwrites properties without error.
2. **Missing index nodes:** Index nodes are created on every save via `AddNodesAsync` (idempotent). If the node already exists, it's a no-op merge.
3. **Empty scope:** If a `LearningEntry` has no `AgentId`, no `TeamId`, and `IsGlobal = false`, no index edges are created. The learning is still retrievable by direct `GetAsync` but won't appear in `SearchAsync` results. This is intentional -- the validation in Section 06 prevents this state.
4. **Malformed graph nodes:** The deserialization helper logs a warning and returns null for nodes with missing critical properties. `SearchAsync` filters out null deserializations.
5. **Concurrent access:** `IKnowledgeGraphStore` implementations handle their own thread safety. The `InMemoryLearningsStore` uses `ConcurrentDictionary` for the same reason.

---

## Verification

After implementation, run:

```
dotnet test src/Content/Tests/Infrastructure.AI.KnowledgeGraph.Tests --filter "FullyQualifiedName~Learnings"
```

Expected: All 16 Graph tests and 2 InMemory tests pass. The tests use `InMemoryGraphStore` as the backing store, so no external infrastructure is required.
