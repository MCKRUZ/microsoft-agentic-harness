<!-- PROJECT_CONFIG
runtime: dotnet
test_command: dotnet test src/AgenticHarness.slnx
END_PROJECT_CONFIG -->

<!-- SECTION_MANIFEST
section-01-drift-domain
section-02-learnings-domain
section-03-drift-config
section-04-learnings-config
section-05-drift-interfaces
section-06-learnings-interfaces
section-07-ewma-scorer
section-08-drift-service
section-09-baseline-store
section-10-drift-audit
section-11-decay-service
section-12-learnings-store
section-13-command-handlers
section-14-drift-sse
section-15-learnings-sse
section-16-escalation-bridge
section-17-learnings-bridge
section-18-di-registration
section-19-appsettings
section-20-verification
END_MANIFEST -->

# Implementation Sections Index — Phase 3: Quality Loop

## Dependency Graph

| Section | Depends On | Blocks | Parallelizable |
|---------|------------|--------|----------------|
| section-01-drift-domain | - | 03, 05, 07 | Yes (with 02) |
| section-02-learnings-domain | - | 04, 06, 11 | Yes (with 01) |
| section-03-drift-config | 01 | 07, 08 | Yes (with 04) |
| section-04-learnings-config | 02 | 11, 12 | Yes (with 03) |
| section-05-drift-interfaces | 01 | 07, 08, 09, 10, 14 | Yes (with 06) |
| section-06-learnings-interfaces | 02 | 11, 12, 13, 15 | Yes (with 05) |
| section-07-ewma-scorer | 03, 05 | 08 | No |
| section-08-drift-service | 07 | 16, 17 | No |
| section-09-baseline-store | 05 | 18 | Yes (with 10) |
| section-10-drift-audit | 05 | 18 | Yes (with 09) |
| section-11-decay-service | 04, 06 | 13 | No |
| section-12-learnings-store | 06 | 13 | Yes (with 11) |
| section-13-command-handlers | 06, 11, 12 | 17 | No |
| section-14-drift-sse | 05 | 18 | Yes (with 15) |
| section-15-learnings-sse | 06 | 18 | Yes (with 14) |
| section-16-escalation-bridge | 08 | 18 | Yes (with 17) |
| section-17-learnings-bridge | 08, 13 | 18 | Yes (with 16) |
| section-18-di-registration | 07-17 | 19 | No |
| section-19-appsettings | 03, 04 | 20 | No |
| section-20-verification | all | - | No |

## Execution Order (Batches)

1. **Batch 1** — section-01-drift-domain, section-02-learnings-domain (parallel, no deps)
2. **Batch 2** — section-03-drift-config, section-04-learnings-config (parallel, after batch 1)
3. **Batch 3** — section-05-drift-interfaces, section-06-learnings-interfaces (parallel, after batch 1)
4. **Batch 4** — section-07-ewma-scorer (after 03, 05)
5. **Batch 5** — section-08-drift-service (after 07), section-09-baseline-store, section-10-drift-audit (after 05, parallel with each other)
6. **Batch 6** — section-11-decay-service (after 04, 06), section-12-learnings-store (after 06, parallel with 11)
7. **Batch 7** — section-13-command-handlers (after 06, 11, 12), section-14-drift-sse (after 05), section-15-learnings-sse (after 06)
8. **Batch 8** — section-16-escalation-bridge, section-17-learnings-bridge (parallel, after 08, 13)
9. **Batch 9** — section-18-di-registration (after all implementations)
10. **Batch 10** — section-19-appsettings (after config classes)
11. **Batch 11** — section-20-verification (final)

## Section Summaries

### section-01-drift-domain
Domain models for drift detection: DriftDimension, DriftSeverity, DriftScope, DriftBaseline, DriftScore, DriftEvent, DriftResolution, DriftAuditRecord enums and records.

### section-02-learnings-domain
Domain models for learnings: LearningCategory, DecayClass, LearningScope, LearningEntry, WeightedLearning, LearningSource, LearningProvenance records.

### section-03-drift-config
DriftDetectionConfig class with EWMA lambda, threshold sigmas, baseline window. FluentValidation. Config binding.

### section-04-learnings-config
LearningsConfig class with feedback alpha, ceiling, diversity ratio, shelf lives. FluentValidation. Config binding.

### section-05-drift-interfaces
Application interfaces: IDriftDetectionService, IDriftBaselineStore, IDriftScorer, IDriftAuditStore, IDriftNotificationChannel, IDriftNotifier, IEwmaStateStore. Request/query DTOs.

### section-06-learnings-interfaces
Application interfaces: ILearningsStore, ILearningDecayService, ILearningNotificationChannel. MediatR commands: RememberCommand, RecallQuery, ForgetCommand, ImproveLearningCommand, RecordLearningAccessCommand.

### section-07-ewma-scorer
EwmaDriftScorer implementing IDriftScorer. EWMA calculation, control limits, deviation scoring. EwmaState persistence via IEwmaStateStore. Severity classifier.

### section-08-drift-service
DefaultDriftDetectionService. Full evaluation pipeline: baseline resolution, scoring, severity classification, escalation trigger, audit, notification. DriftMetrics OTel. CompositeDriftNotifier.

### section-09-baseline-store
GraphDriftBaselineStore with deterministic node IDs. InMemoryDriftBaselineStore for testing.

### section-10-drift-audit
JsonlDriftAuditStore with date-partitioned JSONL files and SemaphoreSlim thread safety.

### section-11-decay-service
DefaultLearningDecayService. 3-category freshness calculation (volatile/stable/permanent). Bias-corrected EMA. LearningsPruningBackgroundService.

### section-12-learnings-store
GraphLearningsStore with deterministic node IDs and synthetic index nodes for scope hierarchy search. InMemoryLearningsStore for testing.

### section-13-command-handlers
MediatR handlers: RememberCommandHandler, RecallQueryHandler (embedding-based relevance, feedback blending, diversity injection), ForgetCommandHandler, ImproveLearningCommandHandler, RecordLearningAccessCommandHandler. LearningsMetrics OTel.

### section-14-drift-sse
AG-UI drift event DTOs and AgUiDriftNotifier implementing IDriftNotificationChannel. SSE event emission with graceful no-op.

### section-15-learnings-sse
AG-UI learning event DTOs and AgUiLearningNotifier implementing ILearningNotificationChannel. SSE event emission with graceful no-op.

### section-16-escalation-bridge
DriftEscalationBridge implementing IEscalationNotificationChannel. Filters drift-originated escalation resolutions. Creates learnings from corrections.

### section-17-learnings-bridge
Drift baseline adjustment from high-confidence learnings. ImproveLearningCommand triggers baseline update when feedback weight exceeds threshold.

### section-18-di-registration
DI registration in Infrastructure.AI, Infrastructure.AI.KnowledgeGraph, Presentation.AgentHub DependencyInjection.cs files. AIConfig.cs updates.

### section-19-appsettings
DriftDetection and Learnings config blocks in appsettings.json and appsettings.Development.json.

### section-20-verification
Full build + test suite. Verify 0 errors, no regressions, 80%+ coverage on new code.
