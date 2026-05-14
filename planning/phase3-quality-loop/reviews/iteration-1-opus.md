# Opus Review

**Model:** claude-opus-4
**Generated:** 2026-05-11T08:30:00Z

---

## Overall Assessment

The plan is thorough, well-structured, and follows established Phase 2 patterns faithfully. The section ordering and dependency chain are correct. 18 findings identified, ranging from critical gaps to nice-to-have improvements.

---

## Findings

### 1. EWMA State Persistence is Under-Specified (Section 7 -- Critical Gap)

EWMA value changes on every evaluation. DriftBaseline is immutable. Cannot stuff mutable running state into immutable baseline without creating a new baseline every evaluation. Need separate `EwmaState` record (scope + dimension -> current EWMA value, sample count) and `IEwmaStateStore` interface.

### 2. IKnowledgeGraphStore Lacks Query-by-Type (Sections 8, 9, 12 -- Architectural Mismatch)

Existing `IKnowledgeGraphStore` has no `GetNodesByTypeAsync` or `SearchNodesAsync`. Only: `GetNodeAsync(nodeId)`, `GetNeighborsAsync(nodeId, maxDepth)`, `GetAllNodesAsync()`, `GetNodesByOwnerAsync(ownerId)`. Section 9 needs explicit design for baseline lookup — deterministic node IDs like `"driftbaseline:{scope}:{identifier}"` or synthetic index nodes. Section 12 `SearchAsync` will require O(n) full scan without indexed query.

### 3. LearningEntry Immutability vs. Mutation (Section 13 -- Footgun)

`ImproveLearningCommandHandler` and `RecallQuery` need to mutate `LearningEntry`. With init-only, must use `with {}` expression. `RecallQuery` updating `LastAccessedAt` violates CQRS (query with side effect). Either split access-time into separate command, or document pragmatic violation.

### 4. Missing OpenTelemetry Metrics (All Sections)

Phase 2 has `EscalationMetrics`/`EscalationConventions`. Plan has no equivalent. Need `DriftMetrics`, `LearningsMetrics`, `DriftConventions`/`LearningConventions`.

### 5. Missing Enabled Guard in Service Implementations (Sections 7-8, 11-13)

Both configs have `Enabled` flags but plan never describes what happens when disabled. Either check at service level (no-op) or conditionally register in DI.

### 6. Config Placement -- AIConfig.cs Update Missing (Sections 3-4)

`AIConfig.cs` needs `DriftDetection` and `Learnings` properties. Plan doesn't mention updating it.

### 7. IDriftNotificationChannel vs IEscalationNotificationChannel Naming (Section 5)

Escalation pattern has separate `IEscalationNotifier` (composite) vs `IEscalationNotificationChannel` (individual). Drift plan has composite implementing same interface as individual. Inconsistent.

### 8. RecallQuery Semantic Relevance is Punted (Section 13)

"Simple string matching for v1" will produce terrible results. Existing `IEmbeddingService` is available. Pick one approach and state it.

### 9. Pruning Needs a Background Service (Section 11)

`PruneExpiredAsync` exists but nobody calls it. Need `LearningsPruningHostedService` on timer from `PruneIntervalHours`.

### 10. Diversity Injection Edge Cases (Section 13)

What if total results < 7 (15% rounds to 0)? What defines "non-feedback-optimized"? Minimum count before activation?

### 11. Soft Delete Fields Missing from LearningEntry (Section 12)

`IsDeleted`/`DeleteReason` not in domain model. Handle as graph-node metadata or add to model.

### 12. Thread Safety of JSONL Audit Store (Section 10)

`SemaphoreSlim(1,1)` serializes all writes. Fine for low throughput. If high frequency, needs fire-and-forget queue.

### 13. Escalation Integration Coupling (Section 16)

"Listen via IEscalationNotificationChannel or poll" too vague. Need explicit: `DriftEscalationResolutionHandler` implementing channel, or MediatR notifications. Pick one.

### 14. DimensionVariances Naming (Section 1)

"Variance" = sigma-squared, but EWMA uses sigma. Rename to `DimensionSigmas` or add `sqrt()`.

### 15. Missing Validator Rules for Request DTOs (Section 5)

Request DTOs mentioned but validation rules not detailed.

### 16. LearningScope Resolution Ambiguity (Section 2)

No agent-to-team membership resolution. How does system know agent "X" belongs to team "T"?

### 17. Handler Layer Placement (Section 13)

Existing handlers in `Application.Core/CQRS/`, not `Application.AI.Common`. Pick one.

### 18. No TimeProvider Usage

Project uses `FakeTimeProvider` in 18+ files. Plan uses implicit `DateTimeOffset.UtcNow`. All new services should inject `TimeProvider`.

---

## Priority Classification

**Must fix:** 1, 2, 17, 18, 6
**Should fix:** 4, 9, 8, 13, 3, 14
**Nice to have:** 5, 7, 10, 11, 16, 15, 12
