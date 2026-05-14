# Design: Memory Compliance Layer & Procedural Memory

**Date:** 2026-05-06
**Status:** Approved
**Approach:** Hybrid — first-class temporal fields on domain models, decorator-based compliance orchestration

## Motivation

The harness has strong episodic/semantic memory (IKnowledgeMemory with Remember/Recall/Forget/Improve) and a full RAG pipeline, but two critical gaps remain for enterprise readiness:

1. **No compliance layer** — no retention policies, no right-to-erasure cascade, no audit trail for memory operations. GDPR Article 17 requires that when a user requests erasure, all derived data (graph nodes, feedback weights, vector embeddings) must be purged. The harness currently deletes graph nodes but leaves feedback weights and vector embeddings orphaned.

2. **No procedural memory** — skills are static SKILL.md files. The agent cannot learn which skills work best for which query types, nor accumulate operational amendments from experience.

## Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Priority order | Compliance first, then procedural | Data model changes are structural; procedural layers on top |
| Retention model | TTL + explicit erasure | TTL alone doesn't satisfy GDPR; explicit-only relies on manual cleanup |
| Audit trail | Pluggable `IMemoryAuditSink` with structured-logging default | Full event model (read+write+delete) without opinionating on storage |
| Procedural scope | Skill effectiveness tracking + instruction amendments | Not full self-modification — too risky for a template without domain-specific guardrails |
| Architecture | First-class temporal fields + decorator orchestration | Type-safe data model with composable compliance logic; stores handle storage, decorators handle policy |

## Domain Model Changes

### GraphNode — Three New Fields

```csharp
public DateTimeOffset? CreatedAt { get; init; }
public DateTimeOffset? ExpiresAt { get; init; }
public string? OwnerId { get; init; }
```

All nullable with `init` — existing construction sites compile without changes. `OwnerId` links to the knowledge scope (user/tenant) that created the entity. `CreatedAt` stamped by compliance decorator. `ExpiresAt` computed from `CreatedAt` + applicable retention policy.

### GraphEdge — Same Three Fields

```csharp
public DateTimeOffset? CreatedAt { get; init; }
public DateTimeOffset? ExpiresAt { get; init; }
public string? OwnerId { get; init; }
```

### New Domain Models (Domain.AI/KnowledgeGraph/Models/)

**RetentionPolicy** — configurable per entity type:

```csharp
public record RetentionPolicy
{
    public required string EntityType { get; init; }
    public required TimeSpan RetentionPeriod { get; init; }
    public bool AllowIndefinite { get; init; }
}
```

**ErasureReceipt** — proof of right-to-erasure fulfillment:

```csharp
public record ErasureReceipt
{
    public required string RequestId { get; init; }
    public required string ScopeId { get; init; }
    public required DateTimeOffset RequestedAt { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
    public required int NodesDeleted { get; init; }
    public required int EdgesDeleted { get; init; }
    public required int FeedbackWeightsDeleted { get; init; }
    public required int VectorEmbeddingsDeleted { get; init; }
}
```

**MemoryAuditEvent** — event model for pluggable audit:

```csharp
public record MemoryAuditEvent
{
    public required string EventId { get; init; }
    public required MemoryAuditAction Action { get; init; }
    public required string ActorId { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required string ScopeId { get; init; }
    public IReadOnlyList<string>? AffectedNodeIds { get; init; }
    public IReadOnlyList<string>? AffectedEdgeIds { get; init; }
    public string? Query { get; init; }
    public int? ResultCount { get; init; }
}
```

**MemoryAuditAction** enum (Domain.AI/KnowledgeGraph/):

```csharp
public enum MemoryAuditAction
{
    Remember,
    Recall,
    Forget,
    Improve,
    Erasure
}
```

### New Domain Model (Domain.AI/Skills/)

**SkillAmendment** — learned instruction amendment:

```csharp
public record SkillAmendment
{
    public required string Id { get; init; }
    public required string SkillId { get; init; }
    public required string Content { get; init; }
    public required string LearnedFrom { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public string? OwnerId { get; init; }
}
```

**SkillEffectivenessRecord** (Domain.AI/Skills/):

```csharp
public record SkillEffectivenessRecord
{
    public required string SkillId { get; init; }
    public required string QueryClassification { get; init; }
    public required int SuccessCount { get; init; }
    public required int TotalCount { get; init; }
    public required double AverageQuality { get; init; }
    public double SuccessRate => TotalCount > 0 ? (double)SuccessCount / TotalCount : 0;
}
```

## Application Interfaces

### Compliance Interfaces (Application.AI.Common/Interfaces/KnowledgeGraph/)

**IMemoryAuditSink:**

```csharp
public interface IMemoryAuditSink
{
    Task EmitAsync(MemoryAuditEvent auditEvent, CancellationToken cancellationToken = default);
    Task EmitBatchAsync(IReadOnlyList<MemoryAuditEvent> auditEvents, CancellationToken cancellationToken = default);
}
```

**IErasureOrchestrator:**

```csharp
public interface IErasureOrchestrator
{
    Task<ErasureReceipt> EraseByOwnerAsync(string ownerId, CancellationToken cancellationToken = default);
    Task<ErasureReceipt> EraseByNodeIdsAsync(IReadOnlyList<string> nodeIds, CancellationToken cancellationToken = default);
}
```

**IRetentionPolicyProvider:**

```csharp
public interface IRetentionPolicyProvider
{
    RetentionPolicy GetPolicy(string entityType);
    IReadOnlyList<RetentionPolicy> GetAllPolicies();
}
```

### IKnowledgeGraphStore Extension

New method on existing interface for owner-scoped queries (needed by erasure orchestrator):

```csharp
Task<IReadOnlyList<GraphNode>> GetNodesByOwnerAsync(string ownerId, CancellationToken cancellationToken = default);
```

### IFeedbackStore Extension

New method on existing interface for erasure cascade:

```csharp
Task DeleteWeightsByNodeIdsAsync(IReadOnlyList<string> nodeIds, CancellationToken cancellationToken = default);
```

### Procedural Memory Interfaces (Application.AI.Common/Interfaces/Skills/)

**ISkillEffectivenessTracker:**

```csharp
public interface ISkillEffectivenessTracker
{
    Task RecordOutcomeAsync(string skillId, string queryClassification, bool succeeded,
        double? qualityScore = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SkillEffectivenessRecord>> GetEffectivenessAsync(
        string queryClassification, int topN = 5, CancellationToken cancellationToken = default);
}
```

**ISkillAmendmentProvider:**

```csharp
public interface ISkillAmendmentProvider
{
    Task<IReadOnlyList<SkillAmendment>> GetAmendmentsAsync(string skillId, CancellationToken cancellationToken = default);
    Task AddAmendmentAsync(SkillAmendment amendment, CancellationToken cancellationToken = default);
    Task RemoveAmendmentAsync(string amendmentId, CancellationToken cancellationToken = default);
}
```

## Infrastructure Implementations

### Compliance Decorator — ComplianceAwareGraphStore

Location: `Infrastructure.AI.KnowledgeGraph/Compliance/ComplianceAwareGraphStore.cs`

Wraps any `IKnowledgeGraphStore` (same pattern as `TenantIsolatedGraphStore`):

- **AddNodesAsync:** stamps `CreatedAt` via `TimeProvider`, resolves `OwnerId` from `IKnowledgeScope`, computes `ExpiresAt` from retention policy, emits `Remember` audit event
- **AddEdgesAsync:** same stamping logic
- **DeleteNodeAsync / DeleteEdgeAsync:** emits `Forget` audit event before delegating
- **GetNodeAsync / GetNeighborsAsync / GetTripletsAsync:** filters expired nodes (`ExpiresAt < now`), emits `Recall` audit event with result counts

### Erasure Orchestrator — DefaultErasureOrchestrator

Location: `Infrastructure.AI.KnowledgeGraph/Compliance/DefaultErasureOrchestrator.cs`

Cascading delete across four storage layers:

1. **Graph store** — query nodes where `OwnerId == targetId`, collect IDs, delete all nodes and connected edges
2. **Feedback store** — `DeleteWeightsByNodeIdsAsync` for deleted node IDs
3. **Vector store** — `IVectorStore.DeleteByDocumentIdsAsync()` for any `ChunkIds` referenced by deleted nodes (optional — null-check for deployments without vector storage)
4. **Audit** — emit `ErasureReceipt` + `MemoryAuditEvent(Erasure)`

### Retention Enforcement — RetentionEnforcementService

Location: `Infrastructure.AI.KnowledgeGraph/Compliance/RetentionEnforcementService.cs`

`BackgroundService` running on configurable interval (default: 24h):

1. Query all nodes where `ExpiresAt < DateTimeOffset.UtcNow`
2. Batch delete via `IErasureOrchestrator.EraseByNodeIdsAsync`
3. Log summary: nodes purged, edges purged, duration

### Audit Sink Implementations

Location: `Infrastructure.AI.KnowledgeGraph/Audit/`

- **NoOpAuditSink** — does nothing, default when compliance disabled
- **StructuredLoggingAuditSink** — writes `MemoryAuditEvent` as structured log entries via `ILogger`. One log line per event, all fields as structured properties

### Procedural Memory Implementations

Location: `Infrastructure.AI.KnowledgeGraph/Skills/`

- **GraphSkillEffectivenessTracker** — stores `SkillMetric` nodes in graph. Each node: SkillId, QueryClassification, SuccessCount, TotalCount, AverageQuality. Updated via upsert on `RecordOutcomeAsync`. Queried by classification, ranked by success rate.

- **GraphSkillAmendmentProvider** — stores `SkillAmendment` nodes linked to synthetic `Skill:{skillId}` node via `"amends"` edge. Amendments participate in compliance automatically (they have `OwnerId`, `ExpiresAt`).

### Skill Loading Integration Point

After loading Tier 2 instructions from SKILL.md, call `ISkillAmendmentProvider.GetAmendmentsAsync(skillId)` and append amendment content to `SkillDefinition.Instructions`. Single insertion point — rest of skill system unaware.

## DI Registration

### Decorator Ordering

```
Raw backend (in_memory / postgresql / neo4j)
  -> TenantIsolatedGraphStore (multi-tenant scoping)
    -> ComplianceAwareGraphStore (temporal stamping, audit, expired-node filtering)
```

### Registration Pattern

```csharp
// Audit sink (keyed DI)
services.AddKeyedSingleton<IMemoryAuditSink>("no_op", ...);
services.AddKeyedSingleton<IMemoryAuditSink>("structured_logging", ...);
services.AddSingleton<IMemoryAuditSink>(sp => resolve from config);

// Retention
services.AddSingleton<IRetentionPolicyProvider, ConfigRetentionPolicyProvider>();

// Erasure orchestrator
services.AddScoped<IErasureOrchestrator, DefaultErasureOrchestrator>();

// Compliance decorator (conditional on ComplianceEnabled)
// Retention enforcement background service
services.AddHostedService<RetentionEnforcementService>();

// Skill effectiveness (conditional on SkillEffectivenessEnabled)
services.AddScoped<ISkillEffectivenessTracker, GraphSkillEffectivenessTracker>();

// Skill amendments (conditional on SkillAmendmentsEnabled)
services.AddScoped<ISkillAmendmentProvider, GraphSkillAmendmentProvider>();
```

## Configuration

New fields in `GraphRagConfig`:

```json
{
  "AI": {
    "Rag": {
      "GraphRag": {
        "ComplianceEnabled": true,
        "AuditSinkProvider": "structured_logging",
        "RetentionEnforcementInterval": "1.00:00:00",
        "RetentionPolicies": {
          "Fact": "365.00:00:00",
          "SkillMetric": "180.00:00:00",
          "SkillAmendment": "365.00:00:00",
          "Concept": "730.00:00:00"
        },
        "SkillEffectivenessEnabled": true,
        "SkillAmendmentsEnabled": true
      }
    }
  }
}
```

## Testing Strategy

### Unit Tests

- **ComplianceAwareGraphStore**: verify temporal stamping, expired-node filtering, audit event emission for each operation
- **DefaultErasureOrchestrator**: verify 4-layer cascade with mocked stores, verify receipt accuracy
- **RetentionEnforcementService**: verify expired node detection and batch deletion
- **GraphSkillEffectivenessTracker**: verify upsert semantics, ranking by success rate
- **GraphSkillAmendmentProvider**: verify amendment storage/retrieval/removal, edge creation
- **ConfigRetentionPolicyProvider**: verify policy resolution, default for unknown entity types

### Integration Tests

- **End-to-end erasure**: Remember facts → Erase by owner → Verify graph, feedback, and vector stores are clean
- **Retention lifecycle**: Create nodes with short TTL → Run enforcement → Verify expiration
- **Amendment loading**: Add amendment → Load skill → Verify instructions contain amendment text
- **Decorator chain**: Full decorator stack (tenant isolation + compliance) with in-memory backend

## File Inventory

### New Files

| File | Layer | Purpose |
|------|-------|---------|
| `Domain.AI/KnowledgeGraph/MemoryAuditAction.cs` | Domain | Audit action enum |
| `Domain.AI/KnowledgeGraph/Models/RetentionPolicy.cs` | Domain | Retention policy record |
| `Domain.AI/KnowledgeGraph/Models/ErasureReceipt.cs` | Domain | Erasure proof record |
| `Domain.AI/KnowledgeGraph/Models/MemoryAuditEvent.cs` | Domain | Audit event record |
| `Domain.AI/Skills/SkillAmendment.cs` | Domain | Learned instruction amendment |
| `Domain.AI/Skills/SkillEffectivenessRecord.cs` | Domain | Skill performance record |
| `Application.AI.Common/Interfaces/KnowledgeGraph/IMemoryAuditSink.cs` | Application | Audit sink interface |
| `Application.AI.Common/Interfaces/KnowledgeGraph/IErasureOrchestrator.cs` | Application | Erasure orchestrator interface |
| `Application.AI.Common/Interfaces/KnowledgeGraph/IRetentionPolicyProvider.cs` | Application | Retention policy interface |
| `Application.AI.Common/Interfaces/Skills/ISkillEffectivenessTracker.cs` | Application | Skill tracking interface |
| `Application.AI.Common/Interfaces/Skills/ISkillAmendmentProvider.cs` | Application | Amendment provider interface |
| `Infrastructure.AI.KnowledgeGraph/Compliance/ComplianceAwareGraphStore.cs` | Infrastructure | Compliance decorator |
| `Infrastructure.AI.KnowledgeGraph/Compliance/DefaultErasureOrchestrator.cs` | Infrastructure | Erasure orchestrator |
| `Infrastructure.AI.KnowledgeGraph/Compliance/RetentionEnforcementService.cs` | Infrastructure | Background TTL enforcement |
| `Infrastructure.AI.KnowledgeGraph/Compliance/ConfigRetentionPolicyProvider.cs` | Infrastructure | Config-based retention policies |
| `Infrastructure.AI.KnowledgeGraph/Audit/NoOpAuditSink.cs` | Infrastructure | No-op audit sink |
| `Infrastructure.AI.KnowledgeGraph/Audit/StructuredLoggingAuditSink.cs` | Infrastructure | Logging audit sink |
| `Infrastructure.AI.KnowledgeGraph/Skills/GraphSkillEffectivenessTracker.cs` | Infrastructure | Graph-backed skill tracking |
| `Infrastructure.AI.KnowledgeGraph/Skills/GraphSkillAmendmentProvider.cs` | Infrastructure | Graph-backed amendments |

### Modified Files

| File | Change |
|------|--------|
| `Domain.AI/KnowledgeGraph/Models/GraphNode.cs` | Add `CreatedAt`, `ExpiresAt`, `OwnerId` |
| `Domain.AI/KnowledgeGraph/Models/GraphEdge.cs` | Add `CreatedAt`, `ExpiresAt`, `OwnerId` |
| `Application.AI.Common/Interfaces/KnowledgeGraph/IKnowledgeGraphStore.cs` | Add `GetNodesByOwnerAsync` |
| `Application.AI.Common/Interfaces/KnowledgeGraph/IFeedbackStore.cs` | Add `DeleteWeightsByNodeIdsAsync` |
| `Infrastructure.AI.KnowledgeGraph/InMemory/InMemoryGraphStore.cs` | Implement `GetNodesByOwnerAsync` |
| `Infrastructure.AI.KnowledgeGraph/Neo4j/Neo4jGraphStore.cs` | Implement `GetNodesByOwnerAsync` |
| `Infrastructure.AI.KnowledgeGraph/PostgreSql/PostgreSqlGraphStore.cs` | Implement `GetNodesByOwnerAsync` |
| `Infrastructure.AI.KnowledgeGraph/Feedback/GraphFeedbackStore.cs` | Implement `DeleteWeightsByNodeIdsAsync` |
| `Infrastructure.AI.KnowledgeGraph/DependencyInjection.cs` | Register all new services |
| `Domain.Common/Config/AI/RAG/GraphRagConfig.cs` | Add compliance and procedural memory config fields |
