---
paths: src/Content/**/Infrastructure.AI.RAG/**/*.cs, src/Content/**/Infrastructure.AI.KnowledgeGraph/**/*.cs, src/Content/**/Governance/**/*.cs, src/Content/**/SkillTraining/**/*.cs
---
# RAG & Knowledge Architecture

The harness includes a full RAG pipeline (`Infrastructure.AI.RAG`) and a production knowledge graph layer (`Infrastructure.AI.KnowledgeGraph`) inspired by [Cognee](https://github.com/topoteretes/cognee).

## RAG Capabilities
- **Ingestion**: 3 chunking strategies (structure-aware, fixed-size, semantic), contextual enrichment (Anthropic pattern), RAPTOR hierarchical summarization
- **Retrieval**: Hybrid dense+sparse via Reciprocal Rank Fusion, query transformation (RAG Fusion, HyDE), query classification/routing
- **Quality**: CRAG evaluation with refinement loops, configurable accept/refine/reject thresholds
- **Assembly**: Token budget enforcement, pointer expansion (sibling/parent), citation tracking
- **Reranking**: Azure Semantic, Cross-Encoder, NoOp (strategy-keyed DI)
- **Stores**: Azure AI Search + FAISS (vector), Azure AI Search + SQLite FTS5 (BM25)
- **Complexity Routing** (Phase A): LLM-based query complexity classification, tiered pipeline selection, 30-50% cost reduction on mixed workloads
- **Multi-Hop** (Phase B): Query decomposition, iterative retrieval with sufficiency evaluation, answer faithfulness evaluation for hallucination detection
- **Full Autonomy** (Phase D): Multi-source parallel orchestration (vector + BM25 + graph), retrieval cost tracking, quality gates at each pipeline stage

## Knowledge Graph (Implemented — from Cognee analysis)
1. **Production Graph Backend** — Neo4j, Kuzu, and PostgreSQL backends behind `IGraphDatabaseBackend`. Entity extraction, Leiden community detection (`LeidenCommunityDetector`), in-memory store for development
2. **Feedback-Weighted Search** — `GraphFeedbackStore` + `LlmFeedbackDetector` track retrieval quality on graph nodes/edges. Future retrievals blend semantic relevance with historical feedback weights
3. **Cross-Session Knowledge Persistence** — `Remember()`/`Recall()`/`Forget()`/`Improve()` via `IKnowledgeMemory`. `InMemorySessionCache` for fast reads with background sync to `ICrossSessionMemoryStore`. `MemoryDecayService` handles configurable decay tiers (CRITICAL/STANDARD/EPHEMERAL)
4. **Entity-Level Provenance** — `DefaultProvenanceStamper` stamps every node/edge with source pipeline, task, and timestamp. `ComplianceAwareGraphStore` enforces retention policies. `DefaultErasureOrchestrator` handles right-to-erasure with `ErasureReceipt` records
5. **Multi-Tenant Knowledge Isolation** — `TenantIsolatedGraphStore` enforces per-record boundaries by **tenant AND owner** via `IKnowledgeScopeValidator`: a record is visible only when its tenant matches (or is global/null) and its owner matches (or is shared-in-tenant/null). Identity is captured at the entry point (`KnowledgeScopeMiddleware`/`KnowledgeScopeHubFilter`) and flows ambiently (`AsyncLocal`) into child scopes + post-turn background writes; `ComplianceAwareGraphStore` stamps `TenantId` on write (owner stays writer-authoritative). Memory is scope-namespaced (`memory:{tenant}:{user}:{key}`). Enforced across **all three backends** — in-memory, Neo4j, and PostgreSQL all persist and filter `OwnerId`/`TenantId` (Postgres self-initializes its schema)

## Governance Subsystems
- **Drift Detection**: EWMA-based quality monitoring against baselines, three severity levels, DriftEscalationBridge
- **Learnings**: CQRS-based knowledge capture with exponential decay, scheduled pruning, drift integration
- **Escalation**: Multi-approval workflows (AllOf/AnyOf/Quorum), JSONL audit, AG-UI notifications
- **Autonomy Tiers**: Manual/Supervised/Autonomous enforcement via MediatR pipeline behavior, response sanitizers
- **Resilience**: Polly circuit breakers, provider fallback chains, health state tracking

## Skill Training (SkillOpt port)
Single-skill-document optimizer modeled on [microsoft/SkillOpt](https://github.com/microsoft/SkillOpt). 6-stage loop (rollout → reflect → aggregate → select → apply → gate) in `Application.AI.Common/CQRS/SkillTraining/TrainSkill/`. Bounded `Edit{Op,Target,Content}` operations (Append/InsertAfter/Replace/Delete) against the SKILL.md, validated by a gate that uses Hard/Soft/Mixed metric projection and strict-greater accept semantics. Epoch boundary runs SlowUpdate (paired longitudinal forgetting detection) and MetaSkillUpdate (cross-epoch strategy memory via `IKnowledgeMemory`). Pure components (`PatchApplier`, `GateEvaluator`, `PatchAggregator`, `TopKEditSelector`, 3 LR schedulers) are stateless. `IPatchProposer` + `IRolloutRunner` ship with `NotConfigured*` fail-fast defaults — consumers replace with agent-backed Infrastructure impls.
