# Infrastructure.AI.KnowledgeGraph

## What This Project Is

Infrastructure.AI.KnowledgeGraph gives AI agents persistent, structured memory that survives across conversations. Instead of forgetting everything when a session ends, agents can Remember facts, Recall relevant knowledge from past sessions, Forget obsolete information, and Improve their knowledge quality based on feedback. The knowledge is stored as a graph -- entities (nodes) connected by relationships (edges) -- which naturally represents how information relates to other information.

The problem it solves: without persistent memory, every agent conversation starts from zero. An agent that helped you debug a production issue last week has no recollection of the architecture, the decisions made, or the context discovered. Knowledge graphs enable agents to accumulate expertise over time, with multiple backend options (in-memory for testing, PostgreSQL for simplicity, Neo4j for production graph workloads).

This project depends on Application.AI.Common (for knowledge graph interfaces) and Domain.Common (for configuration). It is referenced by Infrastructure.AI.RAG (which uses graph-backed retrieval) and Presentation hosts that register the DI extensions.

**Analogy:** If the agent is a consultant, the knowledge graph is their personal wiki -- searchable, updatable, organized by relationships, and accumulated across every engagement.

## Architecture Context

```
Application.AI.Common/Interfaces/KnowledgeGraph/
  IKnowledgeGraphStore          IKnowledgeMemory
  IFeedbackStore                ISessionKnowledgeCache
  IFeedbackDetector             IProvenanceStamper
  IKnowledgeScope               IKnowledgeScopeValidator
       |
       v
+--------------------------------------------------------+
|         Infrastructure.AI.KnowledgeGraph                |
|                                                         |
|  Graph Backends (keyed DI):                            |
|    "in_memory"  --> InMemoryGraphStore                  |
|    "postgresql" --> PostgreSqlGraphStore                |
|    "neo4j"      --> Neo4jGraphStore                     |
|                                                         |
|  Memory Layer:                                         |
|    KnowledgeMemoryService (Remember/Recall/Forget/Improve)|
|    InMemorySessionCache (fast session-local lookup)     |
|                                                         |
|  Feedback Layer:                                       |
|    GraphFeedbackStore (weight persistence)              |
|    LlmFeedbackDetector (signal extraction)             |
|                                                         |
|  Provenance:                                           |
|    DefaultProvenanceStamper (source + timestamp)        |
|                                                         |
|  Multi-Tenancy:                                        |
|    KnowledgeScopeAccessor (per-request scope)          |
|    KnowledgeScopeValidator (permission checks)          |
|    TenantIsolatedGraphStore (scope-enforced decorator)  |
+--------------------------------------------------------+
         ^
         |
  services.AddKnowledgeGraphDependencies(appConfig);
```

## Key Concepts

### Knowledge Graph Store (Multi-Backend)

**What it is:** The persistence layer for entity-relationship data, with three interchangeable backends.

**Why it exists:** Different deployment environments need different graph databases. Development uses in-memory (zero setup). Simple production uses PostgreSQL (which teams likely already have). High-scale graph workloads use Neo4j (purpose-built for traversals).

**How it works:**
- All three backends implement `IKnowledgeGraphStore` with operations: `AddNodesAsync`, `AddEdgesAsync`, `GetNodeAsync`, `GetNeighborsAsync`, `GetTripletsAsync`, `DeleteNodeAsync`, `DeleteEdgeAsync`, `NodeExistsAsync`, `GetNodeCountAsync`, `GetEdgeCountAsync`.
- Registered with keyed DI (`"in_memory"`, `"postgresql"`, `"neo4j"`).
- The default (non-keyed) registration resolves from `AppConfig.AI.Rag.GraphRag.GraphProvider`.

**Neo4j implementation details:**
- Uses Cypher queries over the Bolt protocol via `Neo4j.Driver`
- Connection string parsed from `GraphRag.ConnectionString` (format: `bolt://user:password@host:7687`)
- Nodes stored as `:Entity` labels with `id`, `name`, `type`, `properties` (JSON), `chunk_ids` (list)
- Edges stored as `:RELATES` relationships with `id`, `predicate`, `properties` (JSON), `chunk_id`
- All operations instrumented with `ActivitySource` for OpenTelemetry tracing

### Cross-Session Knowledge Memory

**What it is:** A high-level API (`IKnowledgeMemory`) that provides four operations: Remember, Recall, Forget, Improve.

**Why it exists:** Raw graph operations (add node, traverse edges) are too low-level for agent consumption. The memory service provides intent-based operations that agents can call naturally: "remember that the user prefers TypeScript" or "recall what we discussed about the deployment pipeline."

**How it works:**
1. **Remember(key, content, entityType):** Creates a graph node with ID `memory:{key}` and stores it in the session cache for fast subsequent access.
2. **Recall(query, maxResults):** Two-source lookup:
   - First checks session cache (sub-millisecond, covers recent memories)
   - Then searches the graph store (key lookups, neighbor traversals, entity matching)
   - Deduplicates and returns combined results
3. **Forget(key):** Removes from both session cache and permanent graph store.
4. **Improve(userMessage, assistantResponse, relevantNodeIds):** Uses `IFeedbackDetector` to analyze whether the user's response indicates positive or negative feedback, then applies feedback weights to the relevant knowledge nodes.

```csharp
// Agent remembers something
await _memory.RememberAsync("user_preference", "Prefers TypeScript over JavaScript", "Preference");

// Agent recalls relevant knowledge
var nodes = await _memory.RecallAsync("TypeScript preferences", maxResults: 5);

// After a turn where user corrects the agent
await _memory.ImproveAsync(userMessage, assistantResponse, relevantNodeIds);
```

### Session Cache

**What it is:** An in-memory cache scoped to the current session/request that provides sub-millisecond knowledge lookups.

**Why it exists:** Graph database queries have latency (network hop, query execution). For knowledge accessed multiple times within a session, the cache eliminates redundant database calls.

**How it works:** `InMemorySessionCache` stores `GraphNode` instances in a dictionary, supports substring matching for `Search()`, and is registered with `Scoped` lifetime so each session gets its own cache. The cache can be flushed to the permanent graph store via `FlushToGraphAsync()`.

### Feedback-Weighted Learning

**What it is:** A system that improves knowledge quality over time by tracking how useful each piece of knowledge was.

**Why it exists:** Not all knowledge is equally valuable. Feedback weighting means knowledge that consistently helps the agent produce good responses gets boosted in future retrievals, while unhelpful knowledge naturally sinks.

**How it works:**
1. `LlmFeedbackDetector` analyzes user-assistant exchanges to detect implicit feedback signals (corrections, praise, follow-up questions indicating confusion).
2. `GraphFeedbackStore` maintains a weight per node, updated using exponential moving average with configurable `FeedbackAlpha`.
3. The RAG pipeline's `FeedbackWeightedScorer` blends these weights into retrieval scores during search.

### Entity-Level Provenance

**What it is:** Every extracted node and edge is stamped with metadata about its origin.

**Why it exists:** Auditing. When an agent cites a fact from the knowledge graph, you need to trace where that fact came from -- which document, which pipeline, which timestamp. Provenance enables trust and debugging.

**How it works:** `DefaultProvenanceStamper` attaches source pipeline name, task ID, and timestamp to graph nodes and edges via their `Properties` dictionary.

### Multi-Tenant Isolation

**What it is:** Access control that ensures agents can only access knowledge within their authorized scope.

**Why it exists:** In multi-user or multi-team deployments, one user's knowledge must not leak to another. Scoping provides boundaries: user-level, dataset-level, and owner-level.

**How it works:**
- `KnowledgeScopeAccessor` (scoped per request) holds the current tenant/user/dataset context.
- `KnowledgeScopeValidator` checks whether a requested scope is accessible given the current context.
- `TenantIsolatedGraphStore` decorates any `IKnowledgeGraphStore` to enforce scope checks on every operation.

## Data Flow

```
Agent calls: _memory.RememberAsync("key", "content")
       |
       v
[Create GraphNode with ID "memory:key"]
       |
       v
[Add to InMemorySessionCache] (fast lookup within session)
       |
       v
[Background: flush to IKnowledgeGraphStore on scope dispose]


Agent calls: _memory.RecallAsync("query", maxResults: 5)
       |
       v
[Search InMemorySessionCache] -- substring match
       |
  (if cache satisfies maxResults) --> return immediately
       |
       v
[Search IKnowledgeGraphStore]
  1. Direct key lookup (memory:{term})
  2. Neighbor traversal from matched nodes
  3. Entity-style ID lookup ({term}:entity)
       |
       v
[Deduplicate + combine results]
       |
       v
Return IReadOnlyList<GraphNode>
```

## Project Structure

```
Infrastructure.AI.KnowledgeGraph/
├── InMemory/
│   └── InMemoryGraphStore.cs        Dictionary-based graph (testing/dev)
├── Neo4j/
│   └── Neo4jGraphStore.cs           Cypher/Bolt production backend
├── PostgreSql/
│   └── PostgreSqlGraphStore.cs      PostgreSQL with JSON columns
├── Memory/
│   ├── KnowledgeMemoryService.cs    Remember/Recall/Forget/Improve API
│   └── InMemorySessionCache.cs      Per-session fast cache
├── Feedback/
│   ├── GraphFeedbackStore.cs        Feedback weight persistence
│   └── LlmFeedbackDetector.cs       LLM-based feedback signal extraction
├── Provenance/
│   └── DefaultProvenanceStamper.cs   Source + timestamp stamping
├── Scoping/
│   ├── KnowledgeScopeAccessor.cs    Per-request scope holder
│   ├── KnowledgeScopeValidator.cs   Permission checking
│   └── TenantIsolatedGraphStore.cs  Scope-enforced decorator
├── DependencyInjection.cs           Keyed DI for backends + memory services
└── Infrastructure.AI.KnowledgeGraph.csproj
```

## Key Types Reference

| Type | Purpose | Implements | Lifetime |
|------|---------|-----------|----------|
| `InMemoryGraphStore` | Dev/test graph backend | `IKnowledgeGraphStore` (keyed: "in_memory") | Singleton |
| `Neo4jGraphStore` | Production graph backend | `IKnowledgeGraphStore` (keyed: "neo4j") | Singleton |
| `PostgreSqlGraphStore` | Mid-scale graph backend | `IKnowledgeGraphStore` (keyed: "postgresql") | Singleton |
| `KnowledgeMemoryService` | High-level memory API | `IKnowledgeMemory` | Scoped |
| `InMemorySessionCache` | Fast per-session cache | `ISessionKnowledgeCache` | Scoped |
| `GraphFeedbackStore` | Feedback weight storage | `IFeedbackStore` | Singleton |
| `LlmFeedbackDetector` | Feedback signal extraction | `IFeedbackDetector` | Singleton |
| `DefaultProvenanceStamper` | Origin metadata stamping | `IProvenanceStamper` | Singleton |
| `KnowledgeScopeAccessor` | Current scope holder | `IKnowledgeScope` | Scoped |
| `KnowledgeScopeValidator` | Scope permission checks | `IKnowledgeScopeValidator` | Singleton |

## Configuration

```jsonc
{
  "AppConfig": {
    "AI": {
      "Rag": {
        "GraphRag": {
          "GraphProvider": "in_memory",        // "in_memory" | "postgresql" | "neo4j"
          "ConnectionString": "bolt://neo4j:password@localhost:7687",  // For neo4j/postgresql
          "FeedbackEnabled": true,             // Enable feedback-weighted learning
          "FeedbackAlpha": 0.1                 // EMA learning rate (0.0-1.0, lower = slower adaptation)
        }
      }
    }
  }
}
```

### Backend-Specific Configuration

**Neo4j:** Connection string format is `bolt://user:password@host:7687`. The driver parses UserInfo for credentials.

**PostgreSQL:** Standard connection string (`Host=...;Database=...;Username=...;Password=...`).

**InMemory:** No configuration required. Data is lost on process restart.

## Common Tasks

### How to Switch Graph Backends

Change `AppConfig.AI.Rag.GraphRag.GraphProvider` from `"in_memory"` to `"neo4j"` or `"postgresql"`. The keyed DI system resolves the correct implementation at runtime. No code changes needed.

### How to Add a New Graph Backend

1. Create a class implementing `IKnowledgeGraphStore` in a new folder.
2. Register with keyed DI in `DependencyInjection.cs`:
```csharp
services.AddKeyedSingleton<IKnowledgeGraphStore>("my_backend", (sp, _) =>
    new MyBackendGraphStore(...));
```
3. Set `GraphProvider` to `"my_backend"` in config.

### How to Debug Knowledge Recall Issues

1. Check if the knowledge exists in the session cache (`InMemorySessionCache.Search()`).
2. Check if the node exists in the graph (`IKnowledgeGraphStore.GetNodeAsync("memory:{key}")`).
3. Verify the node ID format -- memories use `memory:{key}`, entities use `{name}:entity`.
4. Neo4j queries are traced via `ActivitySource` ("Infrastructure.AI.KnowledgeGraph.Neo4j") -- check OTel spans for Cypher execution details.

## Dependencies

**Project References:**
- `Application.AI.Common` -- All knowledge graph interfaces (`IKnowledgeGraphStore`, `IKnowledgeMemory`, `ISessionKnowledgeCache`, `IFeedbackStore`, `IFeedbackDetector`, `IProvenanceStamper`, `IKnowledgeScope`, `IKnowledgeScopeValidator`, `IRagModelRouter`)

**NuGet Packages:**
- `Neo4j.Driver` -- Bolt protocol driver for Neo4j graph database
- `Npgsql` -- PostgreSQL ADO.NET driver
- `Microsoft.Extensions.Logging` -- Structured logging
- `Microsoft.Extensions.Options` -- `IOptionsMonitor<AppConfig>` for runtime config

## Testing

- **Test project:** `Infrastructure.AI.KnowledgeGraph.Tests`
- **Run:** `dotnet test --filter "FullyQualifiedName~Infrastructure.AI.KnowledgeGraph.Tests"`
- **Mock guidance:** Use `InMemoryGraphStore` as a real implementation in tests (it requires no external dependencies). Mock `IRagModelRouter` for `LlmFeedbackDetector` tests. For Neo4j integration tests, use a Docker container (`neo4j:latest`) with Testcontainers.
