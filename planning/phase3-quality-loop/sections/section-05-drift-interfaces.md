# Section 5: Drift Detection Application Interfaces

## Overview

This section defines the application-layer contracts for drift detection. These interfaces live in `Application.AI.Common` and are implemented by infrastructure services in later sections. They include:

- Service interfaces (`IDriftDetectionService`, `IDriftBaselineStore`, `IDriftScorer`, `IDriftAuditStore`, `IEwmaStateStore`)
- Notification interfaces (`IDriftNotificationChannel`, `IDriftNotifier`)
- Request/query DTOs (`DriftEvaluationRequest`, `DriftBaselineUpdateRequest`, `DriftHistoryQuery`, `DriftAuditQuery`)
- The `EwmaState` record for EWMA running state persistence

The notifier/channel split mirrors the composite pattern where `IDriftNotifier` fans out to all registered `IDriftNotificationChannel` instances.

## Dependencies

- **Section 1 (drift-domain):** All domain types must exist in `Domain.AI/DriftDetection/`.
- **Existing codebase:** `Result` and `Result<T>` from `Domain.Common`.

## Downstream Consumers

- Section 7 (EWMA scorer) -- implements `IDriftScorer`, `IEwmaStateStore`
- Section 8 (drift service) -- implements `IDriftDetectionService`, `IDriftNotifier`
- Section 9 (baseline store) -- implements `IDriftBaselineStore`
- Section 10 (audit store) -- implements `IDriftAuditStore`
- Section 14 (drift SSE) -- implements `IDriftNotificationChannel`

---

## File Organization

```
src/Content/Application/Application.AI.Common/Interfaces/DriftDetection/
    IDriftDetectionService.cs
    IDriftBaselineStore.cs
    IDriftScorer.cs
    IDriftAuditStore.cs
    IDriftNotificationChannel.cs
    IDriftNotifier.cs
    IEwmaStateStore.cs
    EwmaState.cs
    DriftEvaluationRequest.cs
    DriftBaselineUpdateRequest.cs
    DriftHistoryQuery.cs
    DriftAuditQuery.cs
    DriftEvaluationRequestValidator.cs
    DriftHistoryQueryValidator.cs
    DriftAuditQueryValidator.cs
    DriftBaselineUpdateRequestValidator.cs
```

---

## Tests First

### Test File: `DriftDetectionDtoTests.cs`

Path: `src/Content/Tests/Application.AI.Common.Tests/Interfaces/DriftDetection/DriftDetectionDtoTests.cs`

```csharp
namespace Application.AI.Common.Tests.Interfaces.DriftDetection;

public sealed class DriftDetectionDtoTests
{
    // Test: DriftEvaluationRequest_ValidRequest_ConstructsSuccessfully
    // Test: DriftBaselineUpdateRequest_RequiresValidScope
    // Test: EwmaState_Construction_WithScopeDimensionAndInitialValues
    // Test: EwmaState_DeterministicId_MatchesExpectedPattern
    //   state.DeterministicId.Should().Be("ewma:Skill:code_review:Faithfulness");
}
```

### Test File: `DriftDetectionDtoValidatorTests.cs`

Path: `src/Content/Tests/Application.AI.Common.Tests/Interfaces/DriftDetection/DriftDetectionDtoValidatorTests.cs`

```csharp
namespace Application.AI.Common.Tests.Interfaces.DriftDetection;

public sealed class DriftDetectionDtoValidatorTests
{
    // Test: DriftEvaluationRequestValidator_EmptyDimensions_Fails
    // Test: DriftEvaluationRequestValidator_EmptyScopeIdentifier_Fails
    // Test: DriftEvaluationRequestValidator_ValidRequest_Passes
    // Test: DriftHistoryQueryValidator_StartAfterEnd_Fails
    // Test: DriftHistoryQueryValidator_ValidRange_Passes
    // Test: DriftHistoryQueryValidator_EmptyScopeIdentifier_Fails
    // Test: DriftHistoryQueryValidator_StartEqualsEnd_Fails  (added during review)
    // Test: DriftAuditQueryValidator_StartAfterEnd_Fails
    // Test: DriftAuditQueryValidator_BothNull_Passes
    // Test: DriftAuditQueryValidator_OnlyStartProvided_Passes  (added during review)
    // Test: DriftAuditQueryValidator_OnlyEndProvided_Passes  (added during review)
    // Test: DriftBaselineUpdateRequestValidator_EmptyScopeIdentifier_Fails
    // Test: DriftBaselineUpdateRequestValidator_ValidRequest_Passes
}

// Actual: 19 tests total (6 DTO + 13 validator), all passing.
```

---

## Interface Specifications

### IDriftDetectionService

```csharp
public interface IDriftDetectionService
{
    Task<Result<DriftScore>> EvaluateDriftAsync(DriftEvaluationRequest request, CancellationToken ct);
    Task<Result<DriftBaseline?>> GetBaselineAsync(DriftScope scope, string scopeIdentifier, CancellationToken ct);
    Task<Result<DriftBaseline>> UpdateBaselineAsync(DriftBaselineUpdateRequest request, CancellationToken ct);
    Task<Result<IReadOnlyList<DriftScore>>> GetDriftHistoryAsync(DriftHistoryQuery query, CancellationToken ct);
}
```

### IDriftBaselineStore

Keyed DI: `"graph"` (default), `"in_memory"` (testing).

```csharp
public interface IDriftBaselineStore
{
    Task<Result> SaveBaselineAsync(DriftBaseline baseline, CancellationToken ct);
    Task<Result<DriftBaseline?>> GetBaselineAsync(DriftScope scope, string scopeIdentifier, CancellationToken ct);
    Task<Result<IReadOnlyList<DriftBaseline>>> GetBaselinesAsync(DriftScope? scope, CancellationToken ct);
}
```

### IDriftScorer

Keyed DI: `"ewma"` (default).

```csharp
public interface IDriftScorer
{
    Task<Result<DriftDimensionScore>> ScoreDimensionAsync(
        DriftDimension dimension, double currentValue, DriftBaseline baseline, CancellationToken ct);
}
```

### IDriftAuditStore

```csharp
public interface IDriftAuditStore
{
    Task<Result> RecordAsync(DriftAuditRecord record, CancellationToken ct);
    Task<Result<IReadOnlyList<DriftAuditRecord>>> GetRecordsAsync(DriftAuditQuery query, CancellationToken ct);
}
```

### IDriftNotificationChannel

Individual notification channel (AG-UI SSE, logging, etc.).

```csharp
public interface IDriftNotificationChannel
{
    Task NotifyDriftDetectedAsync(DriftScore score, CancellationToken ct);
    Task NotifyDriftResolvedAsync(DriftEvent driftEvent, CancellationToken ct);
}
```

### IDriftNotifier

Composite dispatcher consumed by `IDriftDetectionService`.

```csharp
public interface IDriftNotifier
{
    Task NotifyDriftDetectedAsync(DriftScore score, CancellationToken ct);
    Task NotifyDriftResolvedAsync(DriftEvent driftEvent, CancellationToken ct);
}
```

### IEwmaStateStore

```csharp
public interface IEwmaStateStore
{
    Task<Result<EwmaState?>> GetStateAsync(DriftScope scope, string scopeIdentifier, DriftDimension dimension, CancellationToken ct);
    Task<Result> SaveStateAsync(EwmaState state, CancellationToken ct);
    Task<Result<IReadOnlyList<EwmaState>>> GetStatesAsync(DriftScope scope, string scopeIdentifier, CancellationToken ct);
}
```

> **Deviation from plan:** Original spec used raw `Task<EwmaState?>` and `Task<IReadOnlyList<EwmaState>>` 
> for read methods. Changed to `Result<T>` wrappers for consistency with sibling stores 
> (`IDriftBaselineStore`, `IDriftAuditStore`). Decided during code review.

### EwmaState Record

```csharp
public sealed record EwmaState
{
    public required DriftScope Scope { get; init; }
    public required string ScopeIdentifier { get; init; }
    public required DriftDimension Dimension { get; init; }
    public required double CurrentEwma { get; init; }
    public required int SampleCount { get; init; }
    public required DateTimeOffset LastUpdatedAt { get; init; }

    /// <summary>
    /// Deterministic ID for graph node storage: "ewma:{Scope}:{ScopeIdentifier}:{Dimension}".
    /// </summary>
    public string DeterministicId => $"ewma:{Scope}:{ScopeIdentifier}:{Dimension}";
}
```

---

## Request/Query DTOs

### DriftEvaluationRequest

```csharp
public sealed record DriftEvaluationRequest
{
    public required DriftScope Scope { get; init; }
    public required string ScopeIdentifier { get; init; }
    public required IReadOnlyDictionary<DriftDimension, double> Dimensions { get; init; }
}
```

### DriftBaselineUpdateRequest

```csharp
public sealed record DriftBaselineUpdateRequest
{
    public required DriftScope Scope { get; init; }
    public required string ScopeIdentifier { get; init; }
}
```

### DriftHistoryQuery

```csharp
public sealed record DriftHistoryQuery
{
    public required DriftScope Scope { get; init; }
    public required string ScopeIdentifier { get; init; }
    public required DateTimeOffset Start { get; init; }
    public required DateTimeOffset End { get; init; }
}
```

### DriftAuditQuery

```csharp
public sealed record DriftAuditQuery
{
    public DateTimeOffset? Start { get; init; }
    public DateTimeOffset? End { get; init; }
    public DriftAuditRecordType? RecordType { get; init; }
    public Guid? EventId { get; init; }
}
```

---

## Validator Rules

| DTO | Property | Rule |
|-----|----------|------|
| `DriftEvaluationRequest` | `ScopeIdentifier` | NotEmpty |
| `DriftEvaluationRequest` | `Dimensions` | NotEmpty (at least one dimension) |
| `DriftHistoryQuery` | `ScopeIdentifier` | NotEmpty |
| `DriftHistoryQuery` | `Start` | Must be before `End` |
| `DriftAuditQuery` | `Start`/`End` | When both provided, Start < End |
| `DriftBaselineUpdateRequest` | `ScopeIdentifier` | NotEmpty |

## Key Design Decisions

1. **Notifier/Channel split:** Matches escalation pattern exactly. `IDriftNotifier` is composite, `IDriftNotificationChannel` is individual.
2. **`EwmaState` in Application layer:** Application-level persistence concern, not domain. Includes `DeterministicId` tied to graph storage.
3. **`Result` vs nullable:** `Result<T>` for operations that can fail. `GetStateAsync` returns nullable (null = not initialized). Collections return `Result<IReadOnlyList<T>>`.
4. **DTOs co-located with interfaces:** Not MediatR commands -- plain DTOs for service interfaces.
5. **Keyed DI design:** Interfaces designed for keyed DI. Keys documented in XML docs.
