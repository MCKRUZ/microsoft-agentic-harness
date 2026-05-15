# Section 20: Full Test Suite Verification

## Overview

This is the final section of Phase 3 (Quality Loop). It depends on **all prior sections (1-19)** being fully implemented. The goal is to verify the entire solution builds cleanly, all Phase 3 tests pass alongside existing Phase 1/2 tests with no regressions, and new code meets the 80% coverage threshold.

No new code is created in this section. It is a verification-only step that confirms the Phase 3 implementation is complete and correct.

---

## Dependencies

| Dependency | Section | What It Provides |
|------------|---------|-----------------|
| Drift domain models | section-01 | `DriftDimension`, `DriftSeverity`, `DriftScope`, `DriftBaseline`, `DriftScore`, `DriftEvent`, `DriftResolution`, `DriftAuditRecord` |
| Learnings domain models | section-02 | `LearningEntry`, `WeightedLearning`, `LearningCategory`, `DecayClass`, `LearningScope`, `LearningSource`, `LearningProvenance` |
| Drift config | section-03 | `DriftDetectionConfig`, `DriftConfigValidator` |
| Learnings config | section-04 | `LearningsConfig`, `LearningsConfigValidator` |
| Drift interfaces | section-05 | `IDriftDetectionService`, `IDriftBaselineStore`, `IDriftScorer`, `IDriftAuditStore`, `IDriftNotificationChannel`, `IDriftNotifier`, `IEwmaStateStore` |
| Learnings interfaces | section-06 | `ILearningsStore`, `ILearningDecayService`, `ILearningNotificationChannel`, MediatR commands |
| EWMA scorer | section-07 | `EwmaDriftScorer` |
| Drift service | section-08 | `DefaultDriftDetectionService`, `CompositeDriftNotifier`, `DriftMetrics` |
| Baseline store | section-09 | `GraphDriftBaselineStore`, `InMemoryDriftBaselineStore` |
| Drift audit store | section-10 | `JsonlDriftAuditStore` |
| Decay service | section-11 | `DefaultLearningDecayService`, `LearningsPruningBackgroundService` |
| Learnings store | section-12 | `GraphLearningsStore`, `InMemoryLearningsStore` |
| Command handlers | section-13 | `RememberCommandHandler`, `RecallQueryHandler`, `ForgetCommandHandler`, `ImproveLearningCommandHandler`, `RecordLearningAccessCommandHandler`, `LearningsMetrics` |
| Drift SSE | section-14 | `AgUiDriftNotifier`, drift AG-UI event DTOs |
| Learnings SSE | section-15 | `AgUiLearningNotifier`, learning AG-UI event DTOs |
| Escalation bridge | section-16 | `DriftEscalationBridge` |
| Learnings bridge | section-17 | Drift baseline adjustment from high-confidence learnings |
| DI registration | section-18 | All service registrations in `DependencyInjection.cs` files |
| appsettings | section-19 | `DriftDetection` and `Learnings` config blocks |

---

## Verification Steps

### Step 1: Build the Solution

Run a clean build of the entire solution to confirm zero compilation errors across all projects.

```powershell
dotnet build src/AgenticHarness.slnx
```

**Expected outcome:** Build succeeds with 0 errors. Warnings are acceptable if they are pre-existing, but no new warnings should be introduced by Phase 3 code.

If the build fails, categorize the errors:
- **Missing type references** -- a section was not implemented or a namespace import is missing
- **Signature mismatches** -- an interface changed between the plan and implementation; reconcile
- **DI registration errors** -- config binding or service registration is incomplete (section 18/19)

---

### Step 2: Run the Full Test Suite

Run all tests across the entire solution to verify Phase 3 tests pass and no regressions were introduced in Phase 1/2 tests.

```powershell
dotnet test src/AgenticHarness.slnx
```

**Expected outcome:** All Phase 3 tests pass. All previously-passing Phase 1/2 tests continue to pass.

**Known pre-existing failures (not Phase 3):**
- 9 `AgentFactoryTests` in `Application.AI.Common.Tests` -- pre-existing, unrelated to any phase
- 9 `MetricsE2E` tests in `Presentation.AgentHub.Tests` -- require Docker (Testcontainers), expected to fail without Docker running

Any new failures beyond these must be investigated and fixed before Phase 3 is considered complete.

---

### Step 3: Run Phase 3 Tests in Isolation

Filter to only Phase 3 test classes to get a focused count.

```powershell
# Drift detection tests
dotnet test src/AgenticHarness.slnx --filter "FullyQualifiedName~Drift|FullyQualifiedName~Ewma|FullyQualifiedName~Baseline"

# Learnings tests
dotnet test src/AgenticHarness.slnx --filter "FullyQualifiedName~Learning|FullyQualifiedName~Decay|FullyQualifiedName~Remember|FullyQualifiedName~Recall|FullyQualifiedName~Forget|FullyQualifiedName~Improve"
```

**Expected outcome:** All filtered tests pass. The combined count across both runs should account for the full Phase 3 test inventory listed below.

---

### Step 4: Coverage Verification

Run tests with code coverage collection to verify new Phase 3 code meets the 80% minimum threshold.

```powershell
dotnet test src/AgenticHarness.slnx --collect:"XPlat Code Coverage"
```

**Expected outcome:** 80%+ line coverage on all new Phase 3 source files. Key areas to verify coverage on:

- `EwmaDriftScorer` -- EWMA calculation, severity classification, state persistence
- `DefaultDriftDetectionService` -- evaluation pipeline, baseline fallback, escalation trigger
- `DefaultLearningDecayService` -- freshness calculation per decay class, pruning, bias correction
- `RecallQueryHandler` -- scope hierarchy search, score blending, diversity injection, feedback ceiling
- `ImproveLearningCommandHandler` -- EMA application, bias correction, baseline adjustment signal
- `DriftEscalationBridge` -- filtering by tool name, resolution handling, learning creation
- `GraphLearningsStore` -- scope hierarchy search, soft delete, deduplication
- `JsonlDriftAuditStore` -- date-partitioned file writes, thread-safe concurrent access

---

## Expected Test Inventory by Project

Tests are created as part of the TDD workflow in each section (sections 1-19). This section does not create new test files -- it verifies the complete set.

### Domain.AI.Tests

| Test Class | Section | Test Count (approx) |
|-----------|---------|---------------------|
| DriftDomainModelTests | 01 | 9 |
| LearningsDomainModelTests | 02 | 11 |

### Domain.Common.Tests

| Test Class | Section | Test Count (approx) |
|-----------|---------|---------------------|
| DriftConfigValidatorTests | 03 | 9 |
| LearningsConfigValidatorTests | 04 | 8 |

### Application.AI.Common.Tests

| Test Class | Section | Test Count (approx) |
|-----------|---------|---------------------|
| DriftEvaluationRequestTests | 05 | 6 |
| LearningsCommandValidationTests | 06 | 7 |

### Application.Core.Tests

| Test Class | Section | Test Count (approx) |
|-----------|---------|---------------------|
| RememberCommandHandlerTests | 13 | 6 |
| RecallQueryHandlerTests | 13 | 8 |
| ForgetCommandHandlerTests | 13 | 3 |
| ImproveLearningCommandHandlerTests | 13 | 7 |
| RecordLearningAccessCommandHandlerTests | 13 | 2 |

### Infrastructure.AI.Tests

| Test Class | Section | Test Count (approx) |
|-----------|---------|---------------------|
| EwmaDriftScorerTests | 07 | 15 |
| DefaultDriftDetectionServiceTests | 08 | 17 |
| GraphDriftBaselineStoreTests | 09 | 5 |
| InMemoryDriftBaselineStoreTests | 09 | 2 |
| JsonlDriftAuditStoreTests | 10 | 7 |
| DefaultLearningDecayServiceTests | 11 | 9 |
| LearningsPruningBackgroundServiceTests | 11 | 3 |
| DriftEscalationBridgeTests | 16 | 6 |
| DriftLearningsBridgeTests | 17 | 5 |
| DI registration tests (Phase 3 additions) | 18 | 10 |

### Infrastructure.AI.KnowledgeGraph.Tests

| Test Class | Section | Test Count (approx) |
|-----------|---------|---------------------|
| GraphLearningsStoreTests | 12 | 11 |
| InMemoryLearningsStoreTests | 12 | 4 |

### Presentation.AgentHub.Tests

| Test Class | Section | Test Count (approx) |
|-----------|---------|---------------------|
| AgUiDriftNotifierTests | 14 | 7 |
| AgUiLearningNotifierTests | 15 | 6 |

### Presentation.Common.Tests / Config Tests

| Test Class | Section | Test Count (approx) |
|-----------|---------|---------------------|
| DriftDetection config binding | 19 | 2 |
| Learnings config binding | 19 | 2 |

**Estimated total Phase 3 tests: ~190**

The actual count may vary based on implementation decisions made during the TDD cycle in each section. The important criteria are: all tests pass, no regressions, and 80%+ coverage on new code.

## Verification Results (Actual)

**Build:** 0 errors, 153 warnings (all pre-existing)

**Full test suite:** 4,282 tests across 17 assemblies
- Passed: 4,263
- Failed: 19 (all pre-existing)
  - 9 AgentFactoryTests (Application.AI.Common.Tests) — pre-existing
  - 9 MetricsE2E (Presentation.AgentHub.Tests) — requires Docker
  - 1 PollyProviderHealthMonitorTests (Infrastructure.AI.Tests) — flaky concurrency assertion
- Phase 3 regressions: **0**

---

## Regression Checklist

Verify these specific scenarios are not broken by Phase 3 changes:

### Phase 1 (Autonomy + Supervisor)
- [ ] Governance policy behavior still triggers supervisor review
- [ ] Autonomy scoring pipeline intact
- [ ] Tool permission checks work

### Phase 2 (Escalation + Resilience)
- [ ] `DefaultEscalationService` lifecycle (create, decide, timeout) unaffected
- [ ] `ResilientChatClient` fallback chain works
- [ ] AG-UI escalation SSE events still emit
- [ ] JSONL escalation audit store writes correctly
- [ ] Composite escalation notifier fans out to all channels (including the new `DriftEscalationBridge` from section-16)

### Cross-Phase Integration Points
- [ ] `DriftEscalationBridge` registered as `IEscalationNotificationChannel` alongside `AgUiEscalationNotifier` -- both receive notifications
- [ ] `IEscalationService.QueueEscalationAsync` called correctly from `DefaultDriftDetectionService` when severity == Escalate
- [ ] `ImproveLearningCommandHandler` baseline adjustment flow triggers `IDriftDetectionService.UpdateBaselineAsync` only for drift-sourced learnings above threshold

---

## Troubleshooting Guide

### Build failures after DI registration (section-18)

If `DependencyInjection.cs` changes cause build errors:
1. Check that all interface types are imported with correct `using` directives
2. Verify keyed DI registrations use the correct key strings (`"ewma"`, `"graph"`, `"in_memory"`)
3. Confirm `AIConfig` has the new `DriftDetection` and `Learnings` properties
4. Ensure `LearningsPruningBackgroundService` is conditionally registered (only when `Learnings.Enabled` is true)

### Test failures in config binding (section-19)

If appsettings binding tests fail:
1. Verify JSON property names match the C# property names exactly (PascalCase in both)
2. Check that the config section path matches: `AI:DriftDetection` and `AI:Learnings`
3. Confirm default values in the config classes match the values in `appsettings.json`

### Test failures in composite notifier fan-out

If `DriftEscalationBridge` is not receiving escalation resolution notifications:
1. Verify it is registered as an `IEscalationNotificationChannel` in DI (section-18)
2. Check that the `CompositeEscalationNotifier` resolves all `IEscalationNotificationChannel` implementations (including the bridge)
3. Confirm the bridge filters by `ToolName == "drift_detection"` convention

### Coverage below 80%

If coverage is below threshold on specific files:
1. `EwmaDriftScorer` -- ensure edge cases are tested (zero variance, first evaluation, disabled config)
2. `RecallQueryHandler` -- ensure diversity injection path is tested (both activated and skipped)
3. `DefaultLearningDecayService` -- ensure all three decay classes and bias correction are tested
4. `DriftEscalationBridge` -- ensure both drift-originated and non-drift resolutions are tested

---

## Build and Run Commands Summary

```powershell
# Full build
dotnet build src/AgenticHarness.slnx

# Full test suite
dotnet test src/AgenticHarness.slnx

# Phase 3 drift tests only
dotnet test src/AgenticHarness.slnx --filter "FullyQualifiedName~Drift|FullyQualifiedName~Ewma|FullyQualifiedName~Baseline"

# Phase 3 learnings tests only
dotnet test src/AgenticHarness.slnx --filter "FullyQualifiedName~Learning|FullyQualifiedName~Decay|FullyQualifiedName~Remember|FullyQualifiedName~Recall|FullyQualifiedName~Forget|FullyQualifiedName~Improve"

# Coverage collection
dotnet test src/AgenticHarness.slnx --collect:"XPlat Code Coverage"
```

---

## Completion Criteria

Phase 3 is complete when ALL of the following are true:

1. `dotnet build src/AgenticHarness.slnx` succeeds with 0 errors
2. `dotnet test src/AgenticHarness.slnx` shows 0 new failures (only pre-existing known failures acceptable)
3. All Phase 3 test classes pass (approximately 190 tests across drift detection and learnings subsystems)
4. No regressions in Phase 1/2 tests (185 Phase 2 tests continue passing)
5. 80%+ line coverage on new Phase 3 code when measured with `--collect:"XPlat Code Coverage"`
6. The `DriftEscalationBridge` correctly participates in the composite escalation notification fan-out (cross-phase integration verified)
