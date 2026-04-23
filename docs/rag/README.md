# RAG Pipeline — Architecture Overview

## Purpose

A modular, state-of-the-art Retrieval-Augmented Generation pipeline built as a pluggable template for the Microsoft Agentic Harness. Every component is behind an interface, registered via keyed DI, and swappable through configuration alone.

## Architecture Diagram

```
                         ┌─────────────────────────────────┐
                         │        IRagOrchestrator          │
                         │   (top-level entry point)        │
                         └──────────┬───────────────────────┘
                                    │
                    ┌───────────────┼───────────────┐
                    ▼               ▼               ▼
            ┌──────────────┐ ┌────────────┐ ┌──────────────┐
            │ Query Router │ │  GraphRAG  │ │  Corrective  │
            │              │ │  Service   │ │  RAG (CRAG)  │
            └──────┬───────┘ └────────────┘ └──────────────┘
                   │
       ┌───────────┼───────────┐
       ▼           ▼           ▼
 ┌───────────┐ ┌────────┐ ┌────────┐
 │ Classifier│ │  RAG-  │ │  HyDE  │
 │           │ │ Fusion │ │        │
 └───────────┘ └────────┘ └────────┘
                   │
                   ▼
         ┌─────────────────┐
         │ Hybrid Retriever│
         │ (Dense + BM25)  │
         └────────┬────────┘
                  │
        ┌─────────┼─────────┐
        ▼         ▼         ▼
   ┌─────────┐ ┌───────┐ ┌───────┐
   │ Vector  │ │ BM25  │ │  RRF  │
   │ Store   │ │ Store │ │ Merge │
   └─────────┘ └───────┘ └───────┘
                  │
                  ▼
         ┌────────────────┐
         │    Reranker     │
         │ (cross-encoder) │
         └────────┬───────┘
                  │
                  ▼
      ┌───────────────────────┐
      │   Context Assembler   │
      │ (pointer expansion +  │
      │  citation tracking)   │
      └───────────────────────┘
```

## Techniques Implemented

| Technique | Source | Component |
|-----------|--------|-----------|
| **Structure-Aware Chunking** | Proxy-Pointer RAG | `MarkdownStructureExtractor` + `StructureAwareChunker` |
| **Pointer-Based Expansion** | Proxy-Pointer RAG | `PointerChunkExpander` |
| **Contextual Retrieval** | Anthropic (2024) | `ContextualChunkEnricher` |
| **RAG-Fusion** | Multi-query | `RagFusionTransformer` |
| **HyDE** | Hypothetical Document Embeddings | `HydeTransformer` |
| **Hybrid Retrieval** | Dense + Sparse | `HybridRetriever` with RRF |
| **Cross-Encoder Reranking** | BGE/ColBERT | `CrossEncoderReranker` |
| **CRAG** | Corrective RAG | `CragEvaluator` |
| **RAPTOR** | Hierarchical summaries | `RaptorSummarizer` |
| **GraphRAG** | Microsoft (2024) | `ManagedCodeGraphRagService` |
| **Model Tiering** | claude-model-switcher | `RagModelRouter` |

## Layer Placement

```
Domain.AI/RAG/
├── Models/          10 records (DocumentChunk, RetrievalResult, etc.)
├── Enums/           6 enums (ChunkingStrategy, QueryType, etc.)
└── Telemetry/       RagConventions.cs

Domain.Common/Config/AI/RAG/
└── 10 config POCOs  (RagConfig, IngestionConfig, etc.)

Application.AI.Common/
├── Interfaces/RAG/  19 interfaces (IVectorStore, IReranker, etc.)
├── MediatRBehaviors/ RetrievalAuditBehavior
└── OpenTelemetry/   RagIngestionMetrics, RagRetrievalMetrics

Application.Core/CQRS/RAG/
├── IngestDocument/  Command + Handler + Validator
└── SearchDocuments/ Query + Handler + Validator

Infrastructure.AI.RAG/           ← Separate project, all implementations
├── Ingestion/       Parsers, chunkers, enricher, RAPTOR, embeddings
├── Retrieval/       Vector stores, BM25, hybrid, rerankers
├── QueryTransform/  Classifier, RAG-Fusion, HyDE, router
├── Evaluation/      CRAG evaluator
├── Assembly/        Pointer expander, citation tracker, context assembler
├── GraphRag/        ManagedCode.GraphRag integration
├── Orchestration/   RagOrchestrator (top-level coordinator)
└── CostControl/     RagModelRouter (model tiering)

Infrastructure.AI/Tools/
├── DocumentSearchTool.cs   (keyed DI: "document_search")
└── DocumentIngestTool.cs   (keyed DI: "document_ingest")

Presentation.AgentHub/Controllers/
└── DocumentsController.cs  (POST /api/documents/ingest, /search)
```

## Configuration

All RAG behavior is controlled via `AppConfig.AI.Rag` in `appsettings.json`. See [model-tiering.md](model-tiering.md) for cost control configuration and [swapping-providers.md](swapping-providers.md) for replacing backend implementations.

## Agent Integration

RAG tools are registered as keyed `ITool` singletons:

- **`document_search`** — operations: `search`, `search_global`, `search_with_citations`
- **`document_ingest`** — operations: `ingest`, `reindex`

Skills can declare these tools in their `SKILL.md` frontmatter:

```yaml
allowed-tools: ["document_search"]
```

The agent's orchestration loop will then invoke the tool via the standard `IToolConverter` → `AITool` bridge, and the `IRagOrchestrator` handles the full pipeline: classify → transform → retrieve → rerank → evaluate → expand → assemble.

## Telemetry

All pipeline stages emit OpenTelemetry spans and metrics using constants from `RagConventions.cs`. Key metrics:

- `rag.ingestion.chunks_produced` — chunks generated per document
- `rag.retrieval.latency_ms` — retrieval duration histogram
- `rag.crag.action` — corrective action distribution
- `rag.model.tier` — model tier used per operation (for cost tracking)

See the Grafana dashboard `agentFramework.json` for pre-built panels.
