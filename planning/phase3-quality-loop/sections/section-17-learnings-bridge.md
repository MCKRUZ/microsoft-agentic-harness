# Section 17: Drift -> Learnings Integration (Learnings Bridge)

## Overview

This section implements the bridge between the learnings subsystem and the drift detection subsystem. When a learning that originated from a drift event receives enough positive feedback (its `FeedbackWeight` exceeds `LearningsConfig.BaselineAdjustmentThreshold`), the bridge automatically adjusts the drift baseline for the affected scope, records an audit entry, and resolves the drift event with `DriftResolutionType.BaselineAdjusted`.

This prevents false-positive drift alerts when intentional quality changes are validated through feedback. The bridge is a service (`LearningsDriftBridge`) that is invoked after `ImproveLearningCommandHandler` completes. It checks whether the updated learning meets the criteria for baseline adjustment and orchestrates the cross-subsystem side effects.

**Layer:** Infrastructure.AI

---

## Dependencies

| Dependency | Section | What It Provides |
|------------|---------|-----------------|
| Drift domain models | section-01 | `DriftScope`, `DriftEvent`, `DriftResolution`, `DriftResolutionType`, `DriftAuditRecord`, `DriftAuditRecordType` |
| Learnings domain models | section-02 | `LearningEntry`, `LearningSourceType`, `LearningSource` |
| Learnings config | section-04 | `LearningsConfig.BaselineAdjustmentThreshold` |
| Drift interfaces | section-05 | `IDriftDetectionService.UpdateBaselineAsync`, `IDriftAuditStore.RecordAsync`, `DriftBaselineUpdateRequest` |
| Learnings interfaces | section-06 | `ILearningsStore.GetAsync` |
| Drift detection service | section-08 | `DefaultDriftDetectionService` implements `IDriftDetectionService` -- the bridge calls `UpdateBaselineAsync` |
| MediatR command handlers | section-13 | `ImproveLearningCommandHandler` -- the handler that triggers the bridge check after updating `FeedbackWeight` |
| `Result<T>` | existing | `Domain.Common.Result`, `Result<T>` |
| `TimeProvider` | existing | For `ResolvedAt` timestamps |
| `IOptionsMonitor<AppConfig>` | existing | Access to `AppConfig.AI.Learnings` and `AppConfig.AI.DriftDetection` config |
| `IKnowledgeGraphStore` | existing | For retrieving drift event nodes associated with a learning's source |

**Blocks:** section-18 (DI Registration -- the bridge must be registered)

---

## Architecture Decision: Where Does the Bridge Live?

The bridge is a separate service (`ILearningsDriftBridge` / `LearningsDriftBridge`) in `Infrastructure.AI` rather than inlined into `ImproveLearningCommandHandler`. Reasons:

1. **Clean Architecture**: The handler lives in `Application.Core`, which cannot reference `IDriftDetectionService` implementations. The bridge needs infrastructure-layer access.
2. **Single Responsibility**: The handler focuses on EMA weight calculation. The bridge focuses on cross-subsystem side effects.
3. **Testability**: The bridge can be tested independently with mocked drift services, without exercising the full MediatR pipeline.
4. **Optional coupling**: If drift detection is disabled, the bridge no-ops. The handler doesn't need to know about drift at all.

The handler calls the bridge via an injected `ILearningsDriftBridge` interface (defined in `Application.AI.Common`). If the learning doesn't qualify for baseline adjustment, the bridge returns immediately.

---

## Files to Create

| File | Project | Purpose |
|------|---------|---------|
| `src/Content/Application/Application.AI.Common/Interfaces/Learnings/ILearningsDriftBridge.cs` | Application.AI.Common | Interface for the bridge |
| `src/Content/Infrastructure/Infrastructure.AI/Learnings/LearningsDriftBridge.cs` | Infrastructure.AI | Bridge implementation |
| `src/Content/Tests/Infrastructure.AI.Tests/Learnings/LearningsDriftBridgeTests.cs` | Infrastructure.AI.Tests | Tests |

### File to Modify

| File | Project | Change |
|------|---------|--------|
| `src/Content/Application/Application.Core/CQRS/Learnings/ImproveLearningCommandHandler.cs` | Application.Core | Add `ILearningsDriftBridge` injection and call after successful update |

---

## Tests (Write First)

### LearningsDriftBridgeTests.cs

Path: `src/Content/Tests/Infrastructure.AI.Tests/Learnings/LearningsDriftBridgeTests.cs`

```csharp
using Domain.AI.DriftDetection;
using Domain.AI.Learnings;

namespace Infrastructure.AI.Tests.Learnings;

/// <summary>
/// Tests for <see cref="LearningsDriftBridge"/>.
/// Verifies that high-confidence learnings originating from drift events trigger
/// baseline adjustment and drift event resolution.
///
/// Mocks: IDriftDetectionService, IDriftAuditStore, IKnowledgeGraphStore,
///   IOptionsMonitor{AppConfig}, TimeProvider (FakeTimeProvider), ILogger.
/// </summary>
public sealed class LearningsDriftBridgeTests
{
    // Test: CheckAndAdjustBaselineAsync_HighWeight_DriftSource_TriggersBaselineUpdate
    //   Arrange: LearningEntry with Source.SourceType == DriftDetection,
    //     Source.SourceId == "drift-event-123", FeedbackWeight = 0.85 (above threshold 0.8).
    //     Mock IKnowledgeGraphStore.GetNodeAsync("drift-event-123") to return a node with
    //     properties containing serialized DriftEvent (Scope=Skill, ScopeIdentifier="code_review",
    //     affected dimensions: Faithfulness, Relevance).
    //     Mock IDriftDetectionService.UpdateBaselineAsync to return success.
    //   Act: call CheckAndAdjustBaselineAsync(learning, ct).
    //   Assert:
    //     - IDriftDetectionService.UpdateBaselineAsync called once with a DriftBaselineUpdateRequest
    //       matching Scope=Skill, ScopeIdentifier="code_review".
    //     - Result is success.

    // Test: CheckAndAdjustBaselineAsync_HighWeight_NonDriftSource_NoBaselineUpdate
    //   Arrange: LearningEntry with Source.SourceType == HumanCorrection,
    //     FeedbackWeight = 0.9 (above threshold).
    //   Act: call CheckAndAdjustBaselineAsync(learning, ct).
    //   Assert:
    //     - IDriftDetectionService.UpdateBaselineAsync NOT called.
    //     - Result is success (no-op, not failure).

    // Test: CheckAndAdjustBaselineAsync_BelowThreshold_NoBaselineUpdate
    //   Arrange: LearningEntry with Source.SourceType == DriftDetection,
    //     FeedbackWeight = 0.5 (below threshold 0.8).
    //   Act: call CheckAndAdjustBaselineAsync(learning, ct).
    //   Assert:
    //     - IDriftDetectionService.UpdateBaselineAsync NOT called.
    //     - Result is success (no-op).

    // Test: CheckAndAdjustBaselineAsync_BaselineAdjusted_RecordsDriftAuditEntry
    //   Arrange: same as HighWeight_DriftSource test.
    //   Act: call CheckAndAdjustBaselineAsync(learning, ct).
    //   Assert:
    //     - IDriftAuditStore.RecordAsync called once with a DriftAuditRecord where
    //       RecordType == DriftAuditRecordType.BaselineUpdated and
    //       EventId matches the drift event ID from the source.

    // Test: CheckAndAdjustBaselineAsync_BaselineAdjusted_ResolvesDriftEvent
    //   Arrange: same as HighWeight_DriftSource test. DriftEvent node has EventId="evt-456".
    //   Act: call CheckAndAdjustBaselineAsync(learning, ct).
    //   Assert:
    //     - IKnowledgeGraphStore node updated with Resolution property containing:
    //       DriftResolutionType.BaselineAdjusted, ResolutionId = learning.LearningId.ToString(),
    //       ResolvedAt matches FakeTimeProvider time.

    // Test: CheckAndAdjustBaselineAsync_DriftDetectionDisabled_NoOp
    //   Arrange: DriftDetectionConfig.Enabled = false. Learning qualifies for adjustment.
    //   Act: call CheckAndAdjustBaselineAsync(learning, ct).
    //   Assert: no calls to any drift services.

    // Test: CheckAndAdjustBaselineAsync_LearningsDisabled_NoOp
    //   Arrange: LearningsConfig.Enabled = false.
    //   Act: call CheckAndAdjustBaselineAsync(learning, ct).
    //   Assert: no calls to any drift services.

    // Test: CheckAndAdjustBaselineAsync_DriftEventNodeNotFound_LogsWarning_ReturnsSuccess
    //   Arrange: Learning with DriftDetection source, FeedbackWeight above threshold.
    //     IKnowledgeGraphStore.GetNodeAsync returns null (event may have been pruned).
    //   Act: call CheckAndAdjustBaselineAsync(learning, ct).
    //   Assert:
    //     - No baseline update attempted.
    //     - Warning logged.
    //     - Result is success (graceful degradation).

    // Test: CheckAndAdjustBaselineAsync_UpdateBaselineFails_ReturnsFailure
    //   Arrange: Learning qualifies. IDriftDetectionService.UpdateBaselineAsync returns failure.
    //   Act: call CheckAndAdjustBaselineAsync(learning, ct).
    //   Assert: result propagates the failure from UpdateBaselineAsync.

    // Test: CheckAndAdjustBaselineAsync_UsesTimeProviderForResolvedAt
    //   Arrange: FakeTimeProvider set to 2025-06-15T10:00:00Z. Learning qualifies.
    //   Act: call CheckAndAdjustBaselineAsync(learning, ct).
    //   Assert: DriftResolution.ResolvedAt on the updated graph node matches 2025-06-15T10:00:00Z.
}
```

---

## Implementation Details

### ILearningsDriftBridge Interface

Path: `src/Content/Application/Application.AI.Common/Interfaces/Learnings/ILearningsDriftBridge.cs`

```csharp
namespace Application.AI.Common.Interfaces.Learnings;

/// <summary>
/// Bridge that adjusts drift baselines when high-confidence learnings originating
/// from drift events receive sufficient positive feedback.
/// </summary>
/// <remarks>
/// Called by <c>ImproveLearningCommandHandler</c> after updating a learning's feedback weight.
/// The bridge checks whether the learning meets the criteria for baseline adjustment:
/// <list type="number">
///   <item>Learning's <c>Source.SourceType</c> is <c>DriftDetection</c></item>
///   <item>Learning's <c>FeedbackWeight</c> exceeds <c>LearningsConfig.BaselineAdjustmentThreshold</c></item>
/// </list>
/// If both conditions are met, the bridge triggers a drift baseline update for the affected scope
/// and resolves the originating drift event.
/// </remarks>
public interface ILearningsDriftBridge
{
    /// <summary>
    /// Checks whether the given learning qualifies for drift baseline adjustment
    /// and, if so, orchestrates the update, audit, and resolution.
    /// </summary>
    /// <param name="learning">The learning entry after feedback weight update.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// Success if adjustment was performed or was not needed.
    /// Failure if the baseline update itself failed.
    /// </returns>
    Task<Result> CheckAndAdjustBaselineAsync(LearningEntry learning, CancellationToken ct);
}
```

This interface uses `Domain.Common.Result` as the return type and `Domain.AI.Learnings.LearningEntry` as the parameter. It belongs in `Application.AI.Common/Interfaces/Learnings/` alongside `ILearningsStore`, `ILearningDecayService`, and `ILearningNotificationChannel`.

### LearningsDriftBridge Implementation

Path: `src/Content/Infrastructure/Infrastructure.AI/Learnings/LearningsDriftBridge.cs`

Implements `ILearningsDriftBridge`. Lives in `Infrastructure.AI` because it coordinates infrastructure concerns (knowledge graph lookups, drift service calls, audit store writes).

**Constructor dependencies:**
- `IDriftDetectionService` -- for `UpdateBaselineAsync`
- `IDriftAuditStore` -- for recording baseline adjustment audit entries
- `IKnowledgeGraphStore` -- for retrieving and updating drift event nodes
- `IOptionsMonitor<AppConfig>` -- for `LearningsConfig.BaselineAdjustmentThreshold`, `LearningsConfig.Enabled`, `DriftDetectionConfig.Enabled`
- `TimeProvider` -- for `ResolvedAt` timestamps
- `ILogger<LearningsDriftBridge>`

**`CheckAndAdjustBaselineAsync` flow:**

1. **Guard: both subsystems enabled.** Check `LearningsConfig.Enabled` AND `DriftDetectionConfig.Enabled`. If either is false, return `Result.Success()` immediately (no-op).

2. **Guard: source type check.** If `learning.Source.SourceType != LearningSourceType.DriftDetection`, return `Result.Success()` (this learning has nothing to do with drift).

3. **Guard: threshold check.** If `learning.FeedbackWeight < config.AI.Learnings.BaselineAdjustmentThreshold`, return `Result.Success()` (not enough positive feedback yet).

4. **Retrieve drift event.** The learning's `Source.SourceId` contains the drift event ID (set by `DriftEscalationBridge` in section-16 or by drift detection directly). Call `IKnowledgeGraphStore.GetNodeAsync(learning.Source.SourceId)` to retrieve the drift event node.
   - If node is null: log a warning ("Drift event node {SourceId} not found -- may have been pruned. Skipping baseline adjustment.") and return `Result.Success()`. Graceful degradation.

5. **Deserialize drift event data.** The node's `Properties` dictionary contains serialized `DriftEvent` data. Extract `Scope`, `ScopeIdentifier`, and `EventId` from the properties. Use `System.Text.Json.JsonSerializer` to deserialize the relevant fields.

6. **Trigger baseline update.** Build a `DriftBaselineUpdateRequest` with the extracted `Scope` and `ScopeIdentifier`. Call `IDriftDetectionService.UpdateBaselineAsync(request, ct)`.
   - If the update returns failure, return that failure (propagate). The caller (`ImproveLearningCommandHandler`) can log or handle as appropriate.

7. **Record audit.** Create a `DriftAuditRecord`:
   - `RecordId` = `Guid.NewGuid()`
   - `EventId` = extracted drift event ID
   - `RecordType` = `DriftAuditRecordType.BaselineUpdated`
   - `Data` = JSON with learning ID, old/new feedback weight, threshold used
   - `RecordedAt` = `TimeProvider.GetUtcNow()`
   Call `IDriftAuditStore.RecordAsync(auditRecord, ct)`.

8. **Resolve drift event.** Update the drift event graph node to include a `Resolution`:
   - `ResolvedBy` = `DriftResolutionType.BaselineAdjusted`
   - `ResolutionId` = `learning.LearningId.ToString()`
   - `ResolvedAt` = `TimeProvider.GetUtcNow()`
   Update the node properties via `IKnowledgeGraphStore.UpsertNodeAsync` (or equivalent update method). The resolution is serialized into the node's properties alongside the existing drift event data.

9. Return `Result.Success()`.

### Modification to ImproveLearningCommandHandler

Path: `src/Content/Application/Application.Core/CQRS/Learnings/ImproveLearningCommandHandler.cs`

The existing handler (from section-13) must be modified to call the bridge after a successful update. The change is minimal:

1. Add `ILearningsDriftBridge` as a constructor dependency.
2. After step 7 (save via store) and before step 9 (return), add:

```csharp
// Check if this learning update should trigger drift baseline adjustment
var bridgeResult = await _driftBridge.CheckAndAdjustBaselineAsync(updated, ct);
if (!bridgeResult.IsSuccess)
{
    _logger.LogWarning("Drift baseline adjustment failed for learning {LearningId}: {Reason}",
        updated.LearningId, bridgeResult.ErrorMessage);
    // Don't fail the improve command -- the learning was already saved successfully.
    // Bridge failure is a non-critical side effect.
}
```

The bridge failure is logged but does NOT cause the `ImproveLearningCommand` to fail. The learning's feedback weight was already updated successfully. The baseline adjustment is a best-effort cross-subsystem side effect.

---

## Integration Flow Diagram

```
ImproveLearningCommand arrives
    |
    v
ImproveLearningCommandHandler
    |-- EMA weight update (section-13 logic)
    |-- Save updated learning to store
    |-- Call ILearningsDriftBridge.CheckAndAdjustBaselineAsync(updated)
         |
         v
    LearningsDriftBridge
         |-- Is drift detection enabled? No -> return success
         |-- Is source type DriftDetection? No -> return success
         |-- Is FeedbackWeight >= threshold? No -> return success
         |-- Retrieve drift event from knowledge graph
         |-- Build DriftBaselineUpdateRequest from event scope
         |-- Call IDriftDetectionService.UpdateBaselineAsync
         |-- Record DriftAuditRecord (BaselineUpdated)
         |-- Resolve drift event (set DriftResolution on graph node)
         |-- Return success
```

---

## Knowledge Graph Node Updates

The bridge reads and writes drift event graph nodes. The node format (established in section-08):

- **Node ID:** Set by drift detection when creating the event. The learning's `Source.SourceId` is this node ID.
- **Node Type:** `"DriftEvent"`
- **Properties (existing):** Serialized `DriftScore`, severity, scope, scopeIdentifier, detectedAt
- **Properties (added by bridge):** `Resolution` object with `ResolvedBy`, `ResolutionId`, `ResolvedAt`

The bridge must deserialize from and serialize to these properties consistently with what `DefaultDriftDetectionService` writes. Use the same `System.Text.Json` serializer options.

---

## Edge Cases

1. **Drift event already resolved:** If the drift event node already has a `Resolution` property, the bridge should check and skip if already resolved (don't overwrite a prior resolution). Log at Debug level.

2. **Learning improved multiple times past threshold:** The bridge should be idempotent. If the baseline was already adjusted for this drift event, subsequent `ImproveLearningCommand` calls on the same learning should detect the existing resolution and no-op.

3. **Multiple learnings from same drift event:** Different learnings could originate from the same drift event (e.g., if the escalation produced multiple corrections). Each learning independently checks the threshold. The first one to trigger adjustment resolves the event; subsequent ones no-op (edge case 2).

4. **Race condition:** Two `ImproveLearningCommand` calls for different learnings from the same drift event, running concurrently. Both check the node, neither sees a resolution yet. Both attempt to update. This is benign -- `UpsertNodeAsync` is last-writer-wins on the resolution. Both baseline updates would also succeed (second is redundant but not harmful). Log at Warning level if resolution already exists when attempting to set it.

---

## Configuration Values Used

From `LearningsConfig` (section-04):
- `BaselineAdjustmentThreshold` (default: 0.8) -- minimum `FeedbackWeight` to trigger baseline adjustment
- `Enabled` (default: true) -- guard for the entire learnings subsystem

From `DriftDetectionConfig` (section-03):
- `Enabled` (default: true) -- guard for the drift subsystem

---

## Implementation Checklist

1. Create test file `src/Content/Tests/Infrastructure.AI.Tests/Learnings/LearningsDriftBridgeTests.cs` with all test stubs
2. Create interface `src/Content/Application/Application.AI.Common/Interfaces/Learnings/ILearningsDriftBridge.cs`
3. Create implementation `src/Content/Infrastructure/Infrastructure.AI/Learnings/LearningsDriftBridge.cs`
4. Modify `src/Content/Application/Application.Core/CQRS/Learnings/ImproveLearningCommandHandler.cs` to inject and call the bridge
5. Update handler test file `src/Content/Tests/Application.Core.Tests/CQRS/Learnings/ImproveLearningCommandHandlerTests.cs` to verify bridge integration (add test: `Handle_AboveThreshold_DriftSource_CallsBridge`, `Handle_BelowThreshold_DoesNotCallBridge`)
6. Verify build: `dotnet build src/AgenticHarness.slnx`
7. Run tests: `dotnet test src/AgenticHarness.slnx`

## Conventions

- The bridge is registered as `ILearningsDriftBridge -> LearningsDriftBridge` (Singleton) in `Infrastructure.AI/DependencyInjection.cs` (section-18 handles actual registration).
- `TimeProvider` injected everywhere -- never use `DateTimeOffset.UtcNow` directly.
- Bridge failure does not fail the parent command. Learning improvement is always the primary operation; baseline adjustment is a best-effort side effect.
- All public types and methods must have XML documentation. This is a template -- docs are teaching material.
- Namespace: `Infrastructure.AI.Learnings` for the implementation, `Application.AI.Common.Interfaces.Learnings` for the interface.
- Follow the same error-handling pattern as `DriftEscalationBridge` (section-16): catch, log, degrade gracefully.
