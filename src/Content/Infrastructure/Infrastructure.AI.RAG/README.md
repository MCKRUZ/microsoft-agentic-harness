# Infrastructure.AI.RAG

## What This Project Is

Infrastructure.AI.RAG implements a complete RAG (Retrieval-Augmented Generation) pipeline that gives AI agents access to organizational knowledge. When a user asks "What are our Q3 deployment guidelines?", the RAG pipeline finds the relevant documents, extracts the most pertinent passages, validates they actually answer the question, and assembles them into a context package the LLM can use to generate an accurate, cited response -- all without the LLM needing to have been trained on that specific information.

The problem it solves: LLMs have a knowledge cutoff date and no access to private/proprietary information. RAG bridges this gap by retrieving relevant documents at query time and injecting them into the LLM's context. This project implements every stage of that pipeline with production-grade features: multiple chunking strategies, hybrid retrieval (semantic + keyword), multiple reranking options, quality evaluation with refinement loops, and citation tracking.

This project depends on Application.AI.Common (for RAG interfaces and knowledge graph contracts) and Domain.Common (for configuration). It is referenced by Infrastructure.AI (which wraps the orchestrator in a `DocumentSearchTool`) and Presentation hosts that register the DI extensions.

**Analogy:** RAG is a research librarian for the AI agent. Given a question, it searches the library (vector + keyword indices), evaluates whether the found documents actually answer the question (CRAG), fetches related materials (pointer expansion), and prepares a reading package with page references (citations) -- all in milliseconds.

## Architecture Context

```
Application.AI.Common/Interfaces/RAG/
  IRagOrchestrator      IHybridRetriever     IReranker
  IVectorStore          IBm25Store           IChunkingService
  IEmbeddingService     ICragEvaluator       IRagContextAssembler
  IQueryClassifier      IQueryTransformer    IGraphRagService
  IRagModelRouter       IPointerExpander     IFeedbackWeightedScorer
       |
       v
+-------------------------------------------------------+
|              Infrastructure.AI.RAG                      |
|                                                        |
|  Orchestration/  <-- Single entry point (SearchAsync)  |
|  Ingestion/      <-- Document parsing + chunking       |
|  Retrieval/      <-- Vector + BM25 + reranking         |
|  QueryTransform/ <-- Classification + transformation   |
|  Evaluation/     <-- CRAG quality control              |
|  Assembly/       <-- Context assembly + citations      |
|  GraphRag/       <-- Knowledge graph integration       |
+-------------------------------------------------------+
         ^
         |
  services.AddRagDependencies(appConfig);
```

## Key Concepts

### The RAG Pipeline (End to End)

When `IRagOrchestrator.SearchAsync(query)` is called, here is exactly what happens:

```
Step 1: CLASSIFY the query
  LlmQueryClassifier determines: Is this a vector search, keyword search,
  hybrid, or knowledge graph question?
       |
Step 2: TRANSFORM the query (if needed)
  RagFusionTransformer generates 3-5 query variations for broader recall
  OR HydeTransformer generates a hypothetical answer document
       |
Step 3: RETRIEVE candidates
  HybridRetriever runs dense (vector) and sparse (BM25) searches in parallel
  Results merged via Reciprocal Rank Fusion (RRF)
       |
Step 4: RERANK results
  AzureSemanticReranker, CrossEncoderReranker, or NoOpReranker
  Optionally: FeedbackWeightedScorer blends knowledge graph feedback
       |
Step 5: EVALUATE quality (CRAG loop)
  CragEvaluator scores relevance: Accept / Refine / Reject
  If "Refine": modify query and retry from Step 3 (max 2 retries)
  If "Reject": return empty context with explanation
       |
Step 6: ASSEMBLE context
  RagContextAssembler enforces token budget, expands pointer chunks
  (sibling/parent), and tracks citations
       |
  Return: RagAssembledContext (text + citations + metadata)
```

### Ingestion Subsystem

**What it is:** The pipeline that turns raw documents into indexed, searchable chunks.

**Why it exists:** Documents must be broken into appropriately-sized pieces, enriched with context, embedded as vectors, and stored in searchable indices before retrieval can work.

#### Chunking Strategies (3 options, keyed DI)

| Strategy | Key | How It Works | Best For |
|----------|-----|-------------|----------|
| Structure-Aware | `"structure_aware"` | Splits on Markdown headings, preserves section boundaries | Documentation with clear heading hierarchy |
| Fixed-Size | `"fixed_size"` | Token-window chunking with configurable overlap | Unstructured text, code |
| Semantic | `"semantic"` | Detects topic boundaries using embedding similarity | Long-form content with topic shifts |

The default strategy is configured in `AppConfig.AI.Rag.Ingestion.DefaultStrategy`. `ChunkingStrategyResolver` handles runtime selection.

#### Contextual Enrichment

`ContextualChunkEnricher` implements the Anthropic contextual retrieval pattern: each chunk is enriched with surrounding context (the document title, section heading, and a brief summary of what comes before/after). This dramatically improves retrieval accuracy because the chunk carries its own context.

#### RAPTOR Summarization

`RaptorSummarizer` builds hierarchical summaries: groups of chunks are summarized into higher-level "cluster summaries," which are themselves indexed. This enables retrieval at different abstraction levels -- a query about high-level architecture finds cluster summaries, while a query about a specific API finds leaf chunks.

#### Embedding Service

`EmbeddingService` converts text to vector representations using the configured embedding model (via `IRagModelRouter` for cost-tier selection).

### Retrieval Subsystem

#### Hybrid Retriever (Dense + Sparse Fusion)

**What it is:** Combines semantic vector search with keyword-based BM25 search using Reciprocal Rank Fusion.

**Why it exists:** Vector search excels at semantic similarity ("similar meaning") but misses exact keywords. BM25 excels at exact term matching but misses semantic relationships. Hybrid retrieval captures both dimensions.

**How RRF works:**
```
For each result appearing in dense or sparse lists:
  fused_score = 1/(k + rank_in_dense) + 1/(k + rank_in_sparse)

Where k=60 (configurable via RrfK). Higher k flattens rank differences.
```

Both searches run concurrently via `Task.WhenAll`. If one fails, the other provides results gracefully.

#### Vector Stores (2 backends, keyed DI)

| Backend | Key | Technology | Best For |
|---------|-----|-----------|----------|
| Azure AI Search | `"azure_ai_search"` | Azure Cognitive Search vector index | Production cloud deployments |
| FAISS | `"faiss"` | Facebook AI Similarity Search (local) | Development, testing, edge deployments |

#### BM25 Stores (2 backends, keyed DI)

| Backend | Key | Technology | Best For |
|---------|-----|-----------|----------|
| Azure AI Search | `"azure_ai_search"` | Azure Cognitive Search full-text | Production (same index as vector) |
| SQLite FTS5 | `"faiss"` (local fallback) | SQLite full-text search | Local development |

#### Rerankers (3 options, keyed DI)

| Reranker | Key | How It Works | When to Use |
|----------|-----|-------------|-------------|
| Azure Semantic | `"azure_semantic"` | Azure AI Search semantic ranker | Production Azure deployments |
| Cross-Encoder | `"cross_encoder"` | LLM scores query-document relevance | High accuracy, higher latency |
| No-Op | `"none"` | Passthrough (skip reranking) | Low-latency scenarios, testing |

### Query Transform Subsystem

#### Query Classification

`LlmQueryClassifier` asks the LLM to categorize the query and select the optimal retrieval strategy:
- `VectorOnly` -- pure semantic search
- `Bm25Only` -- pure keyword search
- `HybridVectorBm25` -- combined (default)
- `GraphRag` -- knowledge graph traversal

#### Query Transformation

Two transformer strategies:

**RAG Fusion** (`"rag_fusion"`): Generates 3-5 variations of the original query to increase recall. Example: "deployment guidelines" might generate "release process documentation", "production deployment checklist", "CI/CD pipeline requirements".

**HyDE** (`"hyde"` -- Hypothetical Document Embeddings): Generates a hypothetical answer document, embeds it, and uses that embedding for retrieval. Works well when the answer's vocabulary differs significantly from the question's.

### CRAG Evaluation (Quality Control)

**What it is:** Corrective RAG -- an LLM-based quality gate that evaluates whether retrieved documents actually answer the question.

**Why it exists:** Retrieval can return semantically similar but irrelevant content. CRAG prevents the agent from generating hallucinated responses based on tangentially-related passages.

**How it works:**
1. `CragEvaluator.EvaluateAsync(query, candidates)` asks the LLM to score relevance.
2. Three possible outcomes:
   - **Accept** (score >= accept threshold): Results are good, proceed to assembly.
   - **Refine** (score between reject and accept): Results are partial, modify query and retry (max 2 times).
   - **Reject** (score < reject threshold): Results are irrelevant, return empty context with explanation.
3. Weak chunks are identified by ID for optional filtering.

### Assembly Subsystem

#### Context Assembler

`RagContextAssembler` takes reranked results and produces the final context:
1. Enforces a token budget (default 4096 tokens) -- stops adding chunks when budget is exceeded.
2. Calls `PointerChunkExpander` to include sibling and parent chunks for additional context.
3. Uses `CitationTracker` to generate source attributions for each included passage.
4. Returns `RagAssembledContext` with assembled text, total tokens, truncation flag, and citations.

#### Pointer Expansion

`PointerChunkExpander` retrieves neighboring chunks (siblings in the same section, parent chunks from higher in the document hierarchy) to provide fuller context around highly-relevant passages.

### GraphRAG Integration

`ManagedCodeGraphRagService` provides an alternative retrieval path that uses the knowledge graph for entity-relationship-based search and community-level summarization. When the query classifier routes to `RetrievalStrategy.GraphRag`, the orchestrator bypasses vector retrieval entirely and delegates to `GlobalSearchAsync`.

## Data Flow (Vector Pipeline)

```
SearchAsync("What are deployment guidelines?")
       |
       v
[LlmQueryClassifier] --> Strategy: HybridVectorBm25
       |
       v
[RagFusionTransformer] --> 4 query variations
       |
       v
[HybridRetriever.RetrieveAsync()]
  |                    |
  v                    v
[VectorStore]      [Bm25Store]
  |                    |
  v                    v
[30 dense results] [30 sparse results]
       |
       v
[Reciprocal Rank Fusion] --> 10 fused results
       |
       v
[AzureSemanticReranker] --> 10 reranked results
       |
       v
[FeedbackWeightedScorer] --> scores adjusted by history
       |
       v
[CragEvaluator] --> Accept (score: 0.87)
       |
       v
[RagContextAssembler]
  - Token budget: 4096
  - Pointer expansion: siblings + parents
  - Citation tracking
       |
       v
RagAssembledContext {
  AssembledText: "## Deployment Guidelines\n...",
  TotalTokens: 3847,
  WasTruncated: false,
  Citations: [{Source: "deployment-guide.md", Section: "Prerequisites", ...}]
}
```

## Project Structure

```
Infrastructure.AI.RAG/
├── Orchestration/
│   └── RagOrchestrator.cs             Single entry point coordinating all stages
├── Ingestion/
│   ├── MarkdownDocumentParser.cs      Document parsing (IDocumentParser)
│   ├── MarkdownStructureExtractor.cs  Heading/section detection
│   ├── StructureAwareChunker.cs       Chunking by document structure
│   ├── FixedSizeChunker.cs            Token-window chunking
│   ├── SemanticChunker.cs             Embedding-based boundary detection
│   ├── ChunkingStrategyResolver.cs    Runtime strategy selection
│   ├── ContextualChunkEnricher.cs     Anthropic contextual retrieval pattern
│   ├── RaptorSummarizer.cs            Hierarchical cluster summaries
│   ├── EmbeddingService.cs            Vector embedding generation
│   └── RagModelRouter.cs              Cost-tier model selection
├── Retrieval/
│   ├── HybridRetriever.cs             Dense + sparse with RRF
│   ├── AzureAISearchVectorStore.cs    Azure AI Search vector backend
│   ├── FaissVectorStore.cs            FAISS local vector backend
│   ├── AzureAISearchBm25Store.cs      Azure AI Search BM25 backend
│   ├── SqliteFts5Store.cs             SQLite FTS5 local backend
│   ├── VectorStoreFactory.cs          Dynamic provider resolution
│   ├── AzureSemanticReranker.cs       Azure semantic reranking
│   ├── CrossEncoderReranker.cs        LLM cross-encoder reranking
│   ├── NoOpReranker.cs                Passthrough (no reranking)
│   └── FeedbackWeightedScorer.cs      Knowledge graph feedback blending
├── QueryTransform/
│   ├── LlmQueryClassifier.cs          Strategy classification via LLM
│   ├── RagFusionTransformer.cs        Multi-query generation
│   ├── HydeTransformer.cs             Hypothetical document generation
│   └── QueryRouter.cs                 Classification + transformation orchestrator
├── Evaluation/
│   └── CragEvaluator.cs               Accept/Refine/Reject quality gate
├── Assembly/
│   ├── RagContextAssembler.cs         Token budget + expansion + citations
│   ├── PointerChunkExpander.cs        Sibling/parent chunk retrieval
│   └── CitationTracker.cs             Source attribution tracking
├── GraphRag/
│   └── ManagedCodeGraphRagService.cs  Knowledge graph retrieval bridge
├── DependencyInjection.cs             6 registration subsections
└── Infrastructure.AI.RAG.csproj
```

## Key Types Reference

| Type | Purpose | Implements | Lifetime |
|------|---------|-----------|----------|
| **Orchestration** | | | |
| `RagOrchestrator` | Top-level pipeline coordinator | `IRagOrchestrator` | Singleton |
| **Ingestion** | | | |
| `MarkdownDocumentParser` | Document parsing | `IDocumentParser` | Singleton |
| `StructureAwareChunker` | Heading-based chunking | `IChunkingService` (keyed: "structure_aware") | Singleton |
| `FixedSizeChunker` | Token-window chunking | `IChunkingService` (keyed: "fixed_size") | Singleton |
| `SemanticChunker` | Embedding-based chunking | `IChunkingService` (keyed: "semantic") | Singleton |
| `ContextualChunkEnricher` | Chunk context enrichment | `IContextualEnricher` | Singleton |
| `RaptorSummarizer` | Hierarchical summarization | `IRaptorSummarizer` | Singleton |
| `EmbeddingService` | Vector embedding | `IEmbeddingService` | Singleton |
| `RagModelRouter` | Cost-tier model selection | `IRagModelRouter` | Singleton |
| **Retrieval** | | | |
| `HybridRetriever` | Dense + sparse fusion | `IHybridRetriever` | Singleton |
| `AzureAISearchVectorStore` | Azure vector search | `IVectorStore` (keyed: "azure_ai_search") | Singleton |
| `FaissVectorStore` | Local vector search | `IVectorStore` (keyed: "faiss") | Singleton |
| `AzureAISearchBm25Store` | Azure keyword search | `IBm25Store` (keyed: "azure_ai_search") | Singleton |
| `SqliteFts5Store` | Local keyword search | `IBm25Store` (keyed: "faiss") | Singleton |
| `AzureSemanticReranker` | Azure semantic reranking | `IReranker` (keyed: "azure_semantic") | Singleton |
| `CrossEncoderReranker` | LLM-based reranking | `IReranker` (keyed: "cross_encoder") | Singleton |
| `NoOpReranker` | Passthrough | `IReranker` (keyed: "none") | Singleton |
| `FeedbackWeightedScorer` | Feedback blending | `IFeedbackWeightedScorer` | Singleton |
| **Query Transform** | | | |
| `LlmQueryClassifier` | Strategy classification | `IQueryClassifier` | Singleton |
| `RagFusionTransformer` | Multi-query expansion | `IQueryTransformer` (keyed: "rag_fusion") | Singleton |
| `HydeTransformer` | Hypothetical document | `IQueryTransformer` (keyed: "hyde") | Singleton |
| `QueryRouter` | Classification + transformation | -- | Singleton |
| **Evaluation + Assembly** | | | |
| `CragEvaluator` | Quality evaluation | `ICragEvaluator` | Singleton |
| `PointerChunkExpander` | Chunk neighborhood expansion | `IPointerExpander` | Singleton |
| `RagContextAssembler` | Final context assembly | `IRagContextAssembler` | Singleton |
| **GraphRAG** | | | |
| `ManagedCodeGraphRagService` | Graph-based retrieval | `IGraphRagService` | Singleton |

## Configuration

```jsonc
{
  "AppConfig": {
    "AI": {
      "Rag": {
        "Ingestion": {
          "DefaultStrategy": "structure_aware",    // "structure_aware" | "fixed_size" | "semantic"
          "ChunkSize": 512,                        // Token target per chunk
          "ChunkOverlap": 64                       // Overlap tokens between chunks
        },
        "Retrieval": {
          "TopK": 10,                              // Number of results to return
          "RrfK": 60,                              // RRF fusion constant (higher = flatter)
          "EnableHybrid": true                     // false = dense-only retrieval
        },
        "VectorStore": {
          "Provider": "azure_ai_search",           // "azure_ai_search" | "faiss"
          "Endpoint": "https://my-search.search.windows.net",
          "ApiKey": "...",
          "IndexName": "rag-index"
        },
        "Reranker": {
          "Strategy": "azure_semantic"             // "azure_semantic" | "cross_encoder" | "none"
        },
        "GraphRag": {
          "GraphProvider": "in_memory",             // Backend for knowledge graph
          "FeedbackEnabled": true,                 // Enable feedback-weighted scoring
          "FeedbackAlpha": 0.1                     // EMA learning rate
        }
      }
    }
  }
}
```

## Common Tasks

### How to Add a New Vector Store Backend

1. Create a class implementing `IVectorStore` in `Retrieval/`:
```csharp
public sealed class MyVectorStore : IVectorStore
{
    public Task<IReadOnlyList<RetrievalResult>> SearchAsync(
        ReadOnlyMemory<float> queryEmbedding, int topK,
        string? collectionName, CancellationToken ct) { ... }

    public Task IndexAsync(IReadOnlyList<DocumentChunk> chunks, CancellationToken ct) { ... }
}
```

2. Register with keyed DI in `DependencyInjection.cs`:
```csharp
services.AddKeyedSingleton<IVectorStore>("my_backend", (sp, _) =>
    new MyVectorStore(...));
```

3. Set `VectorStore.Provider` to `"my_backend"` in config.

### How to Add a New Chunking Strategy

1. Create a class implementing `IChunkingService` in `Ingestion/`.
2. Register with keyed DI: `services.AddKeyedSingleton<IChunkingService>("my_strategy", ...)`.
3. Set `Ingestion.DefaultStrategy` to `"my_strategy"` in config.

### How to Debug Low Retrieval Quality

1. Check the OTel span `rag.orchestrator.search` for:
   - `rag.retrieval_strategy` -- which strategy was selected
   - `rag.retrieval.chunks_returned` -- how many candidates were found
   - `rag.crag.action` -- whether CRAG accepted or rejected
   - `rag.crag.score` -- the relevance score
2. If CRAG keeps rejecting: check that the vector index contains the expected documents.
3. If retrieval returns 0 results: check `EmbeddingService` is configured and the index name matches.
4. Use `RagRetrievalMetrics.Hits` vs `Queries` ratio to track overall hit rate.

## Dependencies

**Project References:**
- `Application.AI.Common` -- All RAG interfaces (15+ types) plus `IKnowledgeGraphStore`, `IProvenanceStamper`, `IFeedbackStore` from knowledge graph

**NuGet Packages:**
- `Azure.Search.Documents` -- Azure AI Search client (vector + BM25 + semantic ranking)
- `Microsoft.Data.Sqlite` -- SQLite FTS5 for local BM25 search
- `Microsoft.Extensions.Logging` -- Structured logging with pipeline stage context
- `Microsoft.Extensions.Options` -- `IOptionsMonitor<AppConfig>` for runtime config

## Testing

- **Test project:** `Infrastructure.AI.RAG.Tests`
- **Run:** `dotnet test --filter "FullyQualifiedName~Infrastructure.AI.RAG.Tests"`
- **Mock guidance:**
  - Mock `IEmbeddingService` to return deterministic vectors
  - Mock `IRagModelRouter` to return a mock `IChatClient`
  - Use `FaissVectorStore` and `SqliteFts5Store` as real implementations for integration tests (no external dependencies)
  - Use `NoOpReranker` when testing retrieval logic in isolation
  - Mock `SearchClient` for Azure AI Search tests (the DI creates a placeholder when not configured)
