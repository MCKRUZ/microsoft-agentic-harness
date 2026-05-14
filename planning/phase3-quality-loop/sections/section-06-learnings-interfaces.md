# Section 06: Learnings Log Application Interfaces

## Overview

This section defines the Application-layer interfaces and MediatR commands/queries for the learnings subsystem. Files live in two locations:

- **Interfaces:** `src/Content/Application/Application.AI.Common/Interfaces/Learnings/`
- **Commands/Queries:** `src/Content/Application/Application.Core/CQRS/Learnings/`

## Dependencies

| Dependency | Section | What It Provides |
|------------|---------|-----------------|
| Learnings domain models | section-02 | `LearningEntry`, `WeightedLearning`, `LearningCategory`, `DecayClass`, `LearningScope`, `LearningSource`, `LearningProvenance`, `LearningSourceType` |
| `Result` / `Result<T>` | existing | Error handling return type from `Domain.Common` |
| MediatR | existing | `IRequest<T>` for command/query definitions |
| FluentValidation | existing | `AbstractValidator<T>` for command validators |

**Blocked by this section:** section-11 (decay service), section-12 (learnings store), section-13 (command handlers), section-15 (AG-UI learning notifier).

---

## Files to Create

### Interfaces
```
src/Content/Application/Application.AI.Common/Interfaces/Learnings/
    ILearningsStore.cs
    ILearningDecayService.cs
    ILearningNotificationChannel.cs
    LearningSearchCriteria.cs          [DEVIATION: moved here from Application.Core to avoid circular dependency]
```

### MediatR Commands/Queries
```
src/Content/Application/Application.Core/CQRS/Learnings/
    RememberCommand.cs
    RememberCommandValidator.cs
    RecallQuery.cs
    RecallQueryValidator.cs
    ForgetCommand.cs
    ForgetCommandValidator.cs
    ImproveLearningCommand.cs
    ImproveLearningCommandValidator.cs
    RecordLearningAccessCommand.cs
```

---

## Tests

### `LearningsCommandValidationTests.cs`

File: `src/Content/Tests/Application.Core.Tests/CQRS/Learnings/LearningsCommandValidationTests.cs`

**22 tests total (16 planned + 6 added during code review):**

```csharp
namespace Application.Core.Tests.CQRS.Learnings;

public sealed class LearningsCommandValidationTests
{
    // == RememberCommand ==
    // Test: Validate_RememberCommand_ValidInput_NoErrors
    // Test: Validate_RememberCommand_EmptyContent_HasError
    // Test: Validate_RememberCommand_NullContent_HasError
    // Test: Validate_RememberCommand_ScopeHasNoIdentifierAndNotGlobal_HasError
    // Test: Validate_RememberCommand_ScopeIsGlobal_NoError
    // Test: Validate_RememberCommand_NullSource_HasError                         [code review fix]
    // Test: Validate_RememberCommand_NullProvenance_HasError                     [code review fix]
    // Test: Validate_RememberCommand_ProvenanceConfidenceOutOfRange_HasError     [code review fix, Theory×3: -0.1, 1.1, 2.0]

    // == RecallQuery ==
    // Test: Validate_RecallQuery_ValidInput_NoErrors
    // Test: Validate_RecallQuery_EmptyContext_HasError
    // Test: Validate_RecallQuery_EmptyScope_HasError                             [code review fix]
    // Test: Validate_RecallQuery_ZeroMaxResults_HasError
    // Test: Validate_RecallQuery_NegativeMaxResults_HasError

    // == ForgetCommand ==
    // Test: Validate_ForgetCommand_ValidInput_NoErrors
    // Test: Validate_ForgetCommand_EmptyGuid_HasError
    // Test: Validate_ForgetCommand_EmptyReason_HasError

    // == ImproveLearningCommand ==
    // Test: Validate_ImproveLearningCommand_ValidInput_NoErrors
    // Test: Validate_ImproveLearningCommand_FeedbackScoreBelow1_HasError
    // Test: Validate_ImproveLearningCommand_FeedbackScoreAbove5_HasError
    // Test: Validate_ImproveLearningCommand_EmptyGuid_HasError
}
```

### `LearningSearchCriteriaTests.cs`

File: `src/Content/Tests/Application.Core.Tests/CQRS/Learnings/LearningSearchCriteriaTests.cs`

```csharp
namespace Application.Core.Tests.CQRS.Learnings;

public sealed class LearningSearchCriteriaTests
{
    // Test: LearningSearchCriteria_DefaultConstruction_HasNullFilters
    // Test: LearningSearchCriteria_ScopeHierarchyPrecedence_AgentFirst
}
```

---

## Interface Specifications

### ILearningsStore

Keyed DI: `"graph"` (default), `"in_memory"` (testing).

```csharp
public interface ILearningsStore
{
    Task<Result> SaveAsync(LearningEntry learning, CancellationToken ct);
    Task<Result<LearningEntry?>> GetAsync(Guid learningId, CancellationToken ct);
    Task<Result<IReadOnlyList<LearningEntry>>> SearchAsync(LearningSearchCriteria criteria, CancellationToken ct);
    Task<Result> UpdateAsync(LearningEntry learning, CancellationToken ct);
    Task<Result> SoftDeleteAsync(Guid learningId, string reason, CancellationToken ct);
}
```

### ILearningDecayService

```csharp
public interface ILearningDecayService
{
    Task<double> CalculateFreshnessAsync(LearningEntry learning, CancellationToken ct);
    Task<Result<int>> PruneExpiredAsync(CancellationToken ct);
}
```

### ILearningNotificationChannel

```csharp
public interface ILearningNotificationChannel
{
    Task NotifyLearningCapturedAsync(LearningEntry learning, CancellationToken ct);
    Task NotifyLearningAppliedAsync(LearningEntry learning, string agentId, CancellationToken ct);
}
```

---

## MediatR Commands/Queries

### LearningSearchCriteria

```csharp
public record LearningSearchCriteria
{
    public required LearningScope Scope { get; init; }
    public LearningCategory? Category { get; init; }
    public double? MinFeedbackWeight { get; init; }
    public DateTimeOffset? CreatedAfter { get; init; }
    public DateTimeOffset? CreatedBefore { get; init; }
}
```

### RememberCommand

```csharp
public record RememberCommand : IRequest<Result<LearningEntry>>
{
    public required string Content { get; init; }
    public required LearningCategory Category { get; init; }
    public required LearningScope Scope { get; init; }
    public required LearningSource Source { get; init; }
    public required LearningProvenance Provenance { get; init; }
    public DecayClass? DecayClass { get; init; }
}
```

Validator: Content NotEmpty, Scope must have AgentId/TeamId/IsGlobal, Source NotNull, Provenance NotNull, Provenance.Confidence in [0,1] (when Provenance not null).

### RecallQuery

```csharp
public record RecallQuery : IRequest<Result<IReadOnlyList<WeightedLearning>>>
{
    public required string Context { get; init; }
    public required LearningScope Scope { get; init; }
    public int MaxResults { get; init; } = 10;
    public double MinRelevance { get; init; } = 0.0;
}
```

Validator: Context NotEmpty, MaxResults > 0, MinRelevance in [0, 1], Scope must have AgentId/TeamId/IsGlobal.

### ForgetCommand

```csharp
public record ForgetCommand : IRequest<Result>
{
    public required Guid LearningId { get; init; }
    public required string Reason { get; init; }
}
```

Validator: LearningId != Guid.Empty, Reason NotEmpty.

### ImproveLearningCommand

```csharp
public record ImproveLearningCommand : IRequest<Result<LearningEntry>>
{
    public required Guid LearningId { get; init; }
    public required double FeedbackScore { get; init; }
    public string? ReinforcementContent { get; init; }
}
```

Validator: LearningId != Guid.Empty, FeedbackScore in [1.0, 5.0].

### RecordLearningAccessCommand

Fire-and-forget command dispatched by RecallQueryHandler for CQRS-clean access tracking.

```csharp
public record RecordLearningAccessCommand : IRequest<Result>
{
    public required IReadOnlyList<Guid> LearningIds { get; init; }
    public required DateTimeOffset AccessedAt { get; init; }
}
```

No validator needed -- internal fire-and-forget command.

---

## Implementation Checklist

1. Create `src/Content/Application/Application.AI.Common/Interfaces/Learnings/` directory
2. Create `ILearningsStore.cs`, `ILearningDecayService.cs`, `ILearningNotificationChannel.cs`
3. Create `src/Content/Application/Application.Core/CQRS/Learnings/` directory
4. Create all command/query files with validators
5. Create test files
6. Verify build: `dotnet build src/AgenticHarness.slnx`
7. Run tests: `dotnet test src/AgenticHarness.slnx`

## Conventions

- Interface namespace: `Application.AI.Common.Interfaces.Learnings`
- Command namespace: `Application.Core.CQRS.Learnings`
- Full XML docs on all interfaces, methods, commands, and properties
- All interface methods are async with `CancellationToken ct` as last parameter
- `Result<T>` for fallible operations, `Task` for notifications, `Task<double>` for pure calculations
- Collections: `IReadOnlyList<T>` for ordered returns
- Records: immutable with `required` and `init`-only setters
- Validators co-located with commands, auto-discovered by assembly scanning
