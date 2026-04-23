# GraphRAG Setup Guide

## Overview

GraphRAG handles **global and thematic queries** — questions like "What are the main strategic themes across these documents?" that require synthesizing information from many sources rather than finding a single relevant passage.

The harness uses [ManagedCode.GraphRag](https://github.com/nicksoftware/ManagedCode.GraphRag), a native C# port of Microsoft's GraphRAG. No Python service or external process needed.

## How GraphRAG Works

```
Documents ──► Entity Extraction ──► Knowledge Graph ──► Community Detection
                (NER + Relations)     (Nodes + Edges)    (Leiden algorithm)
                                                              │
                                                              ▼
                                                     Community Summaries
                                                              │
                                    ┌─────────────────────────┤
                                    ▼                         ▼
                              Global Search              Local Search
                        (community-level synthesis)  (entity-neighborhood)
```

1. **Entity Extraction:** LLM-based NER extracts entities, relationships, and claims from each document chunk
2. **Graph Construction:** Entities become nodes, relationships become edges
3. **Community Detection:** Hierarchical Leiden algorithm groups related entities into communities at multiple levels
4. **Community Summarization:** LLM generates a summary for each community
5. **Search:**
   - **Global Search:** Queries all community summaries, synthesizes across the full corpus
   - **Local Search:** Starts from matched entities, traverses their neighborhood in the graph

## Configuration

```json
{
  "AppConfig": {
    "AI": {
      "Rag": {
        "GraphRag": {
          "Enabled": true,
          "GraphProvider": "managed_code",
          "ConnectionString": "",
          "CommunityLevel": 2,
          "MaxEntityExtractionTokens": 4096
        }
      }
    }
  }
}
```

### Settings

| Setting | Default | Description |
|---------|---------|-------------|
| `Enabled` | `false` | Enable/disable GraphRAG entirely |
| `GraphProvider` | `managed_code` | Implementation to use (`managed_code` or `custom`) |
| `ConnectionString` | `""` | PostgreSQL connection string for persistent storage. Empty = in-memory |
| `CommunityLevel` | `2` | Depth of community hierarchy (1 = coarse, 5 = fine-grained) |
| `MaxEntityExtractionTokens` | `4096` | Max tokens per entity extraction LLM call |

## Storage Backends

### In-Memory (Development)

Leave `ConnectionString` empty. The graph lives in process memory — fast for development, lost on restart.

### PostgreSQL (Production)

Add the `ManagedCode.GraphRag.Postgres` NuGet package and provide a connection string:

```json
{
  "ConnectionString": "Host=localhost;Database=graphrag;Username=postgres;Password=..."
}
```

The graph persists across restarts. Use the same PostgreSQL instance as your application database or a dedicated one.

## Query Routing

The `IRagOrchestrator` automatically routes queries to GraphRAG when:

1. `IQueryClassifier` classifies the query as `QueryType.GlobalThematic`
2. GraphRAG is enabled in config

You can also force GraphRAG via the `document_search` tool's `search_global` operation.

## Model Tiering

Entity extraction and community summarization are LLM-intensive operations. By default, they route through `IRagModelRouter`:

- Entity extraction maps to operation `"entity_extraction"` (configure tier in `ModelTiering.OperationOverrides`)
- Community summarization maps to operation `"community_summarization"`

Both default to the economy tier (e.g., `gpt-4o-mini`) to control costs during indexing.

## Indexing a Corpus

GraphRAG indexing happens automatically during document ingestion when `GraphRag.Enabled = true`. The `IngestDocumentCommandHandler` calls `IGraphRagService.IndexDocumentAsync()` after chunking and embedding.

To reindex the entire corpus:

```http
POST /api/documents/reindex
Content-Type: application/json

{ "rebuildGraph": true }
```

Or via agent tool:

```
document_ingest(operation: "reindex", rebuildGraph: true)
```

## Adding a Custom GraphRAG Provider

1. Implement `IGraphRagService` in `Infrastructure.AI.RAG/GraphRag/`
2. Register as keyed service:

```csharp
services.AddKeyedSingleton<IGraphRagService>("custom", (sp, _) =>
    new MyCustomGraphRagService(...));
```

3. Set `"GraphProvider": "custom"` in config

The interface requires three methods:
- `IndexDocumentAsync` — extract entities and relationships, update graph
- `GlobalSearchAsync` — community-level synthesis across the full corpus
- `LocalSearchAsync` — entity-neighborhood retrieval for specific queries
