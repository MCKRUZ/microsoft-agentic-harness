# Integration Notes — Opus Review Feedback

## Integrating (13 items)

### 1. EWMA State Persistence (Critical) — INTEGRATE
Add `EwmaState` record and `IEwmaStateStore` interface. The reviewer is correct — EWMA is inherently stateful and mutable, and trying to fit it into the immutable baseline record is wrong. New types go in Section 7, new interface in Section 5.

### 2. IKnowledgeGraphStore Query Gap — INTEGRATE (deterministic ID approach)
Use deterministic node IDs like `"driftbaseline:{scope}:{identifier}"` for baselines and `"learning:{learningId}"` for learnings. This avoids needing new query methods on IKnowledgeGraphStore. For scope-based learnings search, use synthetic index nodes (following GraphSkillEffectivenessTracker pattern). Update Sections 9 and 12.

### 3. CQRS Violation in RecallQuery — INTEGRATE
Split `LastAccessedAt` update into a fire-and-forget `RecordLearningAccessCommand` dispatched after recall results are returned. Clean CQRS separation. Update Section 13.

### 4. Missing OpenTelemetry Metrics — INTEGRATE
Add `DriftMetrics` and `LearningsMetrics` static classes with counters/histograms. Add a Section or note in each implementation section. Consistent with Phase 2 pattern.

### 6. AIConfig.cs Update Missing — INTEGRATE
Explicitly add `AIConfig.cs` update to Section 18 (DI Registration). Two new properties on `AIConfig`.

### 8. RecallQuery Relevance Strategy — INTEGRATE
Use existing `IEmbeddingService` for semantic similarity. It's available in the RAG pipeline. No "simple string matching" fallback — embedding-based from day one. Update Section 13.

### 9. Pruning Background Service — INTEGRATE
Add `LearningsPruningBackgroundService : BackgroundService` that runs on `PruneIntervalHours` timer. Register conditionally in DI when `Learnings.Enabled`. Update Section 11 and 18.

### 13. Escalation Integration Mechanism — INTEGRATE
Create `DriftEscalationBridge` implementing `IEscalationNotificationChannel`. It filters by `ToolName == "drift_detection"` convention and bridges resolution into drift event resolution + learning creation. Explicit, no ambiguity. Update Section 16.

### 14. DimensionVariances Naming — INTEGRATE
Rename to `DimensionSigmas` (standard deviation per dimension). Update Section 1.

### 17. Handler Layer Placement — INTEGRATE
Place in `Application.Core/CQRS/Learnings/` following existing pattern. Update Section 13.

### 18. TimeProvider Injection — INTEGRATE
All new services inject `TimeProvider` instead of using `DateTimeOffset.UtcNow`. Consistent with existing pattern (18+ files use `FakeTimeProvider`). Update all implementation sections.

### 5. Enabled Guard — INTEGRATE
Check `Enabled` at service level with early return `Result.Success` no-op. Simpler than conditional DI registration — services are always registered, just no-op when disabled. Update Sections 7-8, 11-13.

### 7. Notification Channel Naming — INTEGRATE
Add `IDriftNotifier` as the composite interface (separate from `IDriftNotificationChannel`), matching the escalation pattern exactly. Update Section 5 and 8.

## NOT Integrating (5 items)

### 10. Diversity Injection Edge Cases — SKIP
Valid concern but implementation detail, not plan-level. Deep-implement will handle minimum counts and edge cases. The plan correctly describes the concept.

### 11. Soft Delete Fields — SKIP
Handle as graph-node-level metadata (`Properties["IsDeleted"]`, `Properties["DeleteReason"]`). This is a persistence concern, not domain. No domain model change needed.

### 12. JSONL Thread Safety — SKIP
Existing pattern works (escalation audit uses same approach). If throughput becomes an issue, it's a performance optimization, not a design change.

### 15. Request DTO Validator Details — SKIP
Validators are implementation details discovered during TDD. The plan correctly notes FluentValidation is required.

### 16. LearningScope Team Membership — SKIP
For v1, the caller provides the team ID explicitly in the query. No agent-to-team registry needed. The scope on a learning entry is declarative — the creator says which team it belongs to. Resolution is simple matching, not membership lookup.
