# RAG Pipeline — Architecture Overview

## Purpose

A modular, state-of-the-art Retrieval-Augmented Generation pipeline built as a pluggable template for the Microsoft Agentic Harness. Every component is behind an interface, registered via keyed DI, and swappable through configuration alone.

The pipeline has evolved through four phases into a fully autonomous retrieval system:

- **Phase A** — Adaptive complexity routing for cost-aware query handling
- **Phase B** — Multi-hop retrieval with faithfulness evaluation
- **Phase C** — Production graph backends and cross-session memory
- **Phase D** — Full autonomy with multi-source orchestration and quality gates

## Architecture Diagram

```
                         ┌─────────────────────────────────┐
                         │        IRagOrchestrator          │
                         │   (top-level entry point)        │
                         └──────────┬───────────────────────┘
                                    │
              ┌─────────────────────┼─────────────────────┐
              ▼                     ▼                     ▼
   ┌────────────────────┐  ┌──────────────┐   ┌──────────────────┐
   │ Complexity Routing  │  │  Multi-Source │   │   Quality Gates  │
   │ (Phase A)           │  │  Orchestrator │   │   (Phase D)      │
   │                     │  │  (Phase D)    │   │                  │
   │ QueryComplexity     │  │              │   │ RetrievalQuality │
   │ Classifier          │  │ Vector+BM25+ │   │ Evaluator        │
   │ RetrievalDecision   │  │ KnowledgeGraph│   │ CostTracker      │
   │ Gate                │  │ in parallel   │   │                  │
   └─────────┬──────────┘  └──────┬───────┘   └──────────────────┘
             │                     │
             ▼                     ▼
   ┌────────────────────┐  ┌──────────────┐
   │ Query Transform     │  │  GraphRAG    │
   │                     │  │  Service     │
   │ Classifier          │  │  (Phase C)   │
   │ RAG-Fusion          │  │              │
   │ HyDE                │  │ Neo4j/Kuzu/  │
   │ QueryDecomposer(B)  │  │ PostgreSQL   │
   └─────────┬──────────┘  │ Leiden comm.  │
             │              └──────────────┘
             ▼
   ┌────────────────────┐
   │ Retrieval           │
   │                     │
   │ HybridRetriever     │
   │ (Dense + BM25 + RRF)│
   │ IterativeRetriever  │
   │ (Phase B multi-hop) │
   └─────────┬──────────┘
             │
       ┌─────┼─────┐
       ▼     ▼     ▼
   ┌──────┐ ┌───┐ ┌───┐
   │Vector│ │BM25│ │RRF│
   │Store │ │    │ │   │
   └──────┘ └───┘ └───┘
             │
             ▼
   ┌────────────────────┐
   │ Evaluation          │
   │                     │
   │ CragEvaluator       │
   │ SufficiencyEval (B) │
   │ FaithfulnessEval(B) │
   └─────────┬──────────┘
             │
             ▼
   ┌────────────────────┐
   │ Assembly             │
   │                     │
   │ Pointer Expansion   │
   │ Citation Tracking   │
   │ Context Assembly    │
   └─────────────────────┘
```

## Techniques Implemented

| Technique | Source | Component | Phase |
|-----------|--------|-----------|-------|
| **Structure-Aware Chunking** | Proxy-Pointer RAG | `MarkdownStructureExtractor` + `StructureAwareChunker` | Base |
| **Pointer-Based Expansion** | Proxy-Pointer RAG | `PointerChunkExpander` | Base |
| **Contextual Retrieval** | Anthropic (2024) | `ContextualChunkEnricher` | Base |
| **RAG-Fusion** | Multi-query | `RagFusionTransformer` | Base |
| **HyDE** | Hypothetical Document Embeddings | `HydeTransformer` | Base |
| **Hybrid Retrieval** | Dense + Sparse | `HybridRetriever` with RRF | Base |
| **Cross-Encoder Reranking** | BGE/ColBERT | `CrossEncoderReranker` | Base |
| **CRAG** | Corrective RAG | `CragEvaluator` | Base |
| **RAPTOR** | Hierarchical summaries | `RaptorSummarizer` | Base |
| **GraphRAG** | Microsoft (2024) | `ManagedCodeGraphRagService` | Base |
| **Model Tiering** | claude-model-switcher | `RagModelRouter` | Base |
| **Adaptive Complexity Routing** | Cost-aware classification | `QueryComplexityClassifier` + `RetrievalDecisionGate` | A |
| **Multi-Hop Retrieval** | Iterative decomposition | `QueryDecomposer` + `IterativeRetriever` | B |
| **Sufficiency Evaluation** | Evidence completeness | `SufficiencyEvaluator` | B |
| **Faithfulness Evaluation** | Hallucination detection | `AnswerFaithfulnessEvaluator` | B |
| **Production Graph Backends** | Neo4j / Kuzu / PostgreSQL | `IGraphDatabaseBackend` implementations | C |
| **Leiden Community Detection** | Hierarchical clustering | `LeidenCommunityDetector` | C |
| **Cross-Session Memory** | Persistent agent knowledge | `CrossSessionMemoryStore` + `KnowledgeMemoryService` | C |
| **Feedback-Weighted Search** | Quality-informed reranking | `GraphFeedbackStore` + `LlmFeedbackDetector` | C |
| **Multi-Source Orchestration** | Parallel retrieval fusion | `MultiSourceOrchestrator` | D |
| **Retrieval Cost Tracking** | Budget enforcement | `RetrievalCostTracker` | D |
| **Quality Gates** | Per-stage evaluation | `RetrievalQualityEvaluator` | D |

## Layer Placement

```
Domain.AI/RAG/
├── Models/          DocumentChunk, RetrievalResult, ComplexityClassification,
│                    IterativeRetrievalResult, FaithfulnessEvaluation
├── Enums/           ChunkingStrategy, QueryType, QueryComplexity + 4 more
└── Telemetry/       RagConventions.cs

Domain.AI/KnowledgeGraph/
├── Models/          GraphNode, GraphEdge, GraphTriplet, Community, MemoryRecord,
│                    ProvenanceStamp, ErasureReceipt, FeedbackDetectionResult + more
└── Enums/           MemoryOperation, MemoryAuditAction

Domain.Common/Config/AI/RAG/
└── 14 config POCOs  RagConfig, IngestionConfig, ComplexityRoutingConfig,
                     FaithfulnessConfig, CrossSessionMemoryConfig,
                     GraphDatabaseConfig, ModelTieringConfig + more

Application.AI.Common/
├── Interfaces/RAG/  23 interfaces — IVectorStore, IReranker, IRagOrchestrator,
│                    IQueryComplexityClassifier, IRetrievalDecisionGate,
│                    IQueryDecomposer, IIterativeRetriever, ISufficiencyEvaluator,
│                    IAnswerFaithfulnessEvaluator, IMultiSourceOrchestrator + more
├── Interfaces/KnowledgeGraph/
│                    15 interfaces — IKnowledgeGraphStore, IGraphDatabaseBackend,
│                    ICrossSessionMemoryStore, IKnowledgeMemory, IFeedbackStore,
│                    IFeedbackDetector, ICommunityDetector, IProvenanceStamper,
│                    IErasureOrchestrator, IMemoryAuditSink + more
├── MediatRBehaviors/ RetrievalAuditBehavior
└── OpenTelemetry/   RagIngestionMetrics, RagRetrievalMetrics

Application.Core/
├── CQRS/RAG/
│   ├── IngestDocument/  Command + Handler + Validator
│   └── SearchDocuments/ Query + Handler + Validator
└── Workflows/
    ├── KnowledgeGraph/  KgIngestionWorkflow, entity extraction, provenance stamping
    ├── Rag/             GraphRagSearchExecutor
    └── Orchestration/   MultiAgentWorkflow, agent executor factory

Infrastructure.AI.RAG/                ← All RAG implementations
├── Ingestion/       Parsers, chunkers, enricher, RAPTOR, embeddings
├── Retrieval/       Vector stores (FAISS, Azure AI Search), BM25 (SQLite FTS5,
│                    Azure AI Search), hybrid retriever, iterative retriever (Phase B)
├── QueryTransform/  Classifier, RAG-Fusion, HyDE, router, QueryDecomposer (Phase B),
│                    QueryComplexityClassifier (Phase A)
├── Evaluation/      CragEvaluator, SufficiencyEvaluator (B), FaithfulnessEvaluator (B)
├── Assembly/        Pointer expander, citation tracker, context assembler
├── GraphRag/        ManagedCode integration, Kuzu backend (C), Leiden detector (C),
│                    CrossSessionMemoryStore (C), MemoryDecayService (C)
├── Orchestration/   RagOrchestrator (D), RagOrchestrator.MultiHop (D),
│                    MultiSourceOrchestrator (D), RetrievalDecisionGate (A/D),
│                    RetrievalCostTracker (D)
└── CostControl/     RagModelRouter (model tiering)

Infrastructure.AI.KnowledgeGraph/     ← Knowledge graph subsystem (Phase C)
├── InMemory/        InMemoryGraphStore (development)
├── Neo4j/           Neo4jGraphStore (production)
├── PostgreSql/      PostgreSqlGraphStore (production)
├── Memory/          InMemorySessionCache, KnowledgeMemoryService
├── Feedback/        GraphFeedbackStore, LlmFeedbackDetector
├── Compliance/      ComplianceAwareGraphStore, RetentionEnforcementService,
│                    DefaultErasureOrchestrator, ConfigRetentionPolicyProvider
├── Provenance/      DefaultProvenanceStamper
├── Scoping/         TenantIsolatedGraphStore, KnowledgeScopeValidator
├── Audit/           StructuredLoggingAuditSink, NoOpAuditSink
├── Learnings/       GraphLearningsStore, InMemoryLearningsStore
└── Skills/          GraphSkillAmendmentProvider, GraphSkillEffectivenessTracker

Infrastructure.AI/Tools/
├── DocumentSearchTool.cs   (keyed DI: "document_search")
└── DocumentIngestTool.cs   (keyed DI: "document_ingest")

Presentation.AgentHub/Controllers/
└── DocumentsController.cs  (POST /api/documents/ingest, /search)
```

## DI Composition

All RAG and knowledge graph services are registered through extension methods in `DependencyInjection.cs`:

```csharp
// Infrastructure.AI.RAG/DependencyInjection.cs
public static IServiceCollection AddRagDependencies(this IServiceCollection services, AppConfig config)
{
    AddRagIngestion(services, config);           // Chunkers, enricher, RAPTOR, embeddings
    AddRagRetrieval(services, config);           // Vector stores, BM25, hybrid retriever
    AddRagQueryTransform(services, config);      // Classifier, RAG-Fusion, HyDE, router
    AddRagEvaluation(services, config);          // CRAG evaluator
    AddRagGraphDatabase(services, config);       // Phase C: Graph backend selection
    AddRagCrossSessionMemory(services, config);  // Phase C: Memory persistence
    AddRagGraphRag(services, config);            // Phase C: GraphRAG + Leiden
    AddRagComplexityRouting(services);           // Phase A: Complexity classifier + gate
    AddRagMultiHop(services, config);            // Phase B: Iterative retriever + decomposer
    AddRagFaithfulness(services, config);        // Phase B: Faithfulness evaluator
    AddRagOrchestration(services, config);       // Phase D: Main orchestrator
    AddRagMultiSource(services, config);         // Phase D: Multi-source parallel retrieval
    AddRagQualityGates(services, config);        // Phase D: Quality evaluation gates
    return services;
}

// Infrastructure.AI.KnowledgeGraph/DependencyInjection.cs
public static IServiceCollection AddKnowledgeGraphDependencies(this IServiceCollection services, AppConfig config)
{
    // Graph stores, memory, feedback, compliance, provenance, scoping, audit
}
```

## Configuration

All RAG behavior is controlled via `AppConfig.AI.Rag` in `appsettings.json`:

| Config Section | Purpose | Docs |
|----------------|---------|------|
| `Rag.VectorStore` | Vector store provider, endpoint, embedding model | [swapping-providers.md](swapping-providers.md) |
| `Rag.Ingestion` | Chunking strategy, overlap, contextual enrichment | — |
| `Rag.Reranker` | Reranking strategy (Azure Semantic, Cross-Encoder, None) | [swapping-providers.md](swapping-providers.md) |
| `Rag.Crag` | Accept/refine/reject thresholds for corrective RAG | — |
| `Rag.ModelTiering` | Per-operation model tier routing for cost control | [model-tiering.md](model-tiering.md) |
| `Rag.ComplexityRouting` | Query complexity thresholds, tier-based pipeline selection | — |
| `Rag.Faithfulness` | Hallucination detection thresholds, citation requirements | — |
| `Rag.CrossSessionMemory` | Decay rates, retention policies, sync intervals | — |
| `Rag.GraphDatabase` | Backend provider (InMemory/Neo4j/PostgreSQL), connection | — |
| `Rag.GraphRag` | GraphRAG enabled, provider, community level | [graphrag-setup.md](graphrag-setup.md) |

## Agent Integration

RAG tools are registered as keyed `ITool` singletons:

- **`document_search`** — operations: `search`, `search_global`, `search_with_citations`
- **`document_ingest`** — operations: `ingest`, `reindex`

Skills can declare these tools in their `SKILL.md` frontmatter:

```yaml
allowed-tools: ["document_search"]
```

The agent's orchestration loop invokes the tool via the standard `IToolConverter` → `AITool` bridge. The `IRagOrchestrator` handles the full pipeline:

```
classify (complexity) → route (tier) → transform (decompose/fuse/HyDE)
    → retrieve (single-hop or multi-hop, multi-source)
        → rerank → evaluate (CRAG + sufficiency + faithfulness)
            → expand (pointers) → assemble (citations + budget)
```

## Telemetry

All pipeline stages emit OpenTelemetry spans and metrics using constants from `RagConventions.cs`:

| Metric | Description |
|--------|-------------|
| `rag.ingestion.chunks_produced` | Chunks generated per document |
| `rag.retrieval.latency_ms` | Retrieval duration histogram |
| `rag.crag.action` | Corrective action distribution (accept/refine/reject) |
| `rag.model.tier` | Model tier used per operation (for cost tracking) |
| `rag.complexity.classification` | Query complexity tier distribution |
| `rag.multihop.iterations` | Retrieval iterations per multi-hop query |
| `rag.faithfulness.score` | Answer grounding score distribution |
| `rag.cost.per_query` | Estimated cost per retrieval query |

See the Grafana dashboard `agentFramework.json` for pre-built panels.
