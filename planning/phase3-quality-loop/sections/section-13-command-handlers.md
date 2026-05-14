# Section 13: MediatR Command Handlers

## Overview

This section implements the five MediatR command/query handlers that form the learnings subsystem's CQRS operations: `RememberCommandHandler`, `RecallQueryHandler`, `ForgetCommandHandler`, `ImproveLearningCommandHandler`, and `RecordLearningAccessCommandHandler`. It also creates the `LearningsMetrics` static OTel class and the `LearningConventions` telemetry constants.

Handlers live in `Application.Core/CQRS/Learnings/` alongside the commands defined in section-06. The OTel metrics class lives in `Application.AI.Common/OpenTelemetry/Metrics/`. The conventions class lives in `Domain.AI/Telemetry/Conventions/`.

**Layer:** Application.Core (handlers), Application.AI.Common (metrics), Domain.AI (conventions)

---

## Dependencies

| Dependency | Section | What It Provides |
|------------|---------|-----------------|
| Learnings domain models | section-02 | `LearningEntry`, `WeightedLearning`, `LearningCategory`, `DecayClass`, `LearningScope`, `LearningSource`, `LearningProvenance`, `LearningSourceType` |
| Learnings interfaces | section-06 | `ILearningsStore`, `ILearningDecayService`, `ILearningNotificationChannel`, `RememberCommand`, `RecallQuery`, `ForgetCommand`, `ImproveLearningCommand`, `RecordLearningAccessCommand`, `LearningSearchCriteria` |
| Learnings config | section-04 | `LearningsConfig` with `FeedbackAlpha`, `FeedbackCeiling`, `DiversityInjectionRatio`, `BaselineAdjustmentThreshold`, `BiasCorrection`, `Enabled` |
| Decay service interface | section-06/11 | `ILearningDecayService.CalculateFreshnessAsync` |
| Learnings store interface | section-06/12 | `ILearningsStore.SaveAsync`, `GetAsync`, `SearchAsync`, `UpdateAsync`, `SoftDeleteAsync` |
| Embedding service | existing | `IEmbeddingService.EmbedQueryAsync` from `Application.AI.Common.Interfaces.RAG` for semantic similarity in recall |
| `Result<T>` | existing | `Domain.Common.Result`, `Result<T>` for return types |
| `TimeProvider` | existing | Injected for deterministic timestamps |
| `IOptionsMonitor<AppConfig>` | existing | Access to `AppConfig.AI.Learnings` for config values |
| `AppInstrument` | existing | `Domain.Common.Telemetry.AppInstrument.Meter` for creating OTel instruments |
| `EscalationConventions` pattern | existing | Template for `LearningConventions` class structure |
| `EscalationMetrics` pattern | existing | Template for `LearningsMetrics` class structure |

**Blocked by this section:** section-17 (Drift -> Learnings integration bridge).

---

## Files to Create

### Handler Files (Application.Core)
```
src/Content/Application/Application.Core/CQRS/Learnings/
    RememberCommandHandler.cs
    RecallQueryHandler.cs
    ForgetCommandHandler.cs
    ImproveLearningCommandHandler.cs
    RecordLearningAccessCommandHandler.cs
```

### Telemetry Files
```
src/Content/Domain/Domain.AI/Telemetry/Conventions/LearningConventions.cs
src/Content/Application/Application.AI.Common/OpenTelemetry/Metrics/LearningsMetrics.cs
```

### Test Files
```
src/Content/Tests/Application.Core.Tests/CQRS/Learnings/RememberCommandHandlerTests.cs
src/Content/Tests/Application.Core.Tests/CQRS/Learnings/RecallQueryHandlerTests.cs
src/Content/Tests/Application.Core.Tests/CQRS/Learnings/ForgetCommandHandlerTests.cs
src/Content/Tests/Application.Core.Tests/CQRS/Learnings/ImproveLearningCommandHandlerTests.cs
src/Content/Tests/Application.Core.Tests/CQRS/Learnings/RecordLearningAccessCommandHandlerTests.cs
src/Content/Tests/Application.Core.Tests/CQRS/Learnings/LearningsMetricsTests.cs
```

---

## Tests (Write First)

### RememberCommandHandlerTests

File: `src/Content/Tests/Application.Core.Tests/CQRS/Learnings/RememberCommandHandlerTests.cs`

```csharp
namespace Application.Core.Tests.CQRS.Learnings;

/// <summary>
/// Tests for <see cref="RememberCommandHandler"/>.
/// Uses Moq for ILearningsStore, ILearningNotificationChannel.
/// Uses FakeTimeProvider for deterministic timestamps.
/// </summary>
public sealed class RememberCommandHandlerTests
{
    // Test: Handle_ValidInput_SavesLearningToStore
    //   Arrange: build RememberCommand with valid content/category/scope/source/provenance.
    //   Act: invoke handler.
    //   Assert: ILearningsStore.SaveAsync called once with a LearningEntry that has matching
    //     Content, Category, Scope, Source, Provenance, FeedbackWeight==1.0, UpdateCount==0.

    // Test: Handle_FactualCorrection_SetsPermanentDecay
    //   Arrange: command with Category=FactualCorrection and no explicit DecayClass.
    //   Act: invoke handler.
    //   Assert: the saved LearningEntry has DecayClass.Permanent.

    // Test: Handle_StylePreference_SetsStableDecay
    //   Arrange: command with Category=StylePreference and no explicit DecayClass.
    //   Act: invoke handler.
    //   Assert: the saved LearningEntry has DecayClass.Stable.

    // Test: Handle_ExplicitDecayClass_OverridesDefault
    //   Arrange: command with Category=FactualCorrection but DecayClass=Volatile explicitly set.
    //   Act: invoke handler.
    //   Assert: saved entry has DecayClass.Volatile (explicit overrides default mapping).

    // Test: Handle_ValidInput_EmitsLearningCapturedNotification
    //   Arrange: valid command.
    //   Act: invoke handler.
    //   Assert: ILearningNotificationChannel.NotifyLearningCapturedAsync called once.

    // Test: Handle_ValidInput_ReturnsSuccessWithEntry
    //   Act: invoke handler.
    //   Assert: result.IsSuccess is true, result.Value is the created LearningEntry.

    // Test: Handle_Disabled_ReturnsSuccessNoOp
    //   Arrange: LearningsConfig.Enabled = false.
    //   Act: invoke handler.
    //   Assert: result.IsSuccess is true, ILearningsStore.SaveAsync NOT called.

    // Test: Handle_UsesTimeProviderForTimestamps
    //   Arrange: FakeTimeProvider set to a specific time.
    //   Act: invoke handler.
    //   Assert: saved entry's CreatedAt matches the FakeTimeProvider time.

    // Test: Handle_IncrementsLearningsMetricsRemembered
    //   (Verify metric counter increments — can be tested by checking no exception and
    //    using a test meter listener if available, or simply verifying the handler code path completes.)
}
```

### RecallQueryHandlerTests

File: `src/Content/Tests/Application.Core.Tests/CQRS/Learnings/RecallQueryHandlerTests.cs`

```csharp
namespace Application.Core.Tests.CQRS.Learnings;

/// <summary>
/// Tests for <see cref="RecallQueryHandler"/>.
/// Mocks: ILearningsStore, ILearningDecayService, IEmbeddingService, IMediator (for fire-and-forget access command).
/// Uses FakeTimeProvider.
/// </summary>
public sealed class RecallQueryHandlerTests
{
    // Test: Handle_MatchingLearnings_ReturnsSortedByFinalScore
    //   Arrange: store returns 3 learnings. Mock embedding service to return known cosine
    //     similarities (0.9, 0.5, 0.7). Mock decay service to return freshness (1.0, 0.5, 0.8).
    //   Act: invoke handler.
    //   Assert: result contains 3 WeightedLearning records sorted by FinalScore descending.
    //     FinalScore = (1 - alpha) * relevance + alpha * min(feedback * freshness, ceiling).

    // Test: Handle_ScopeHierarchy_MergesAllLevels
    //   Arrange: scope with AgentId="A1" and TeamId="T1". Store returns 2 agent-scoped,
    //     1 team-scoped, 1 global learning (total 4 distinct).
    //   Act: invoke handler.
    //   Assert: result contains all 4 learnings.

    // Test: Handle_FeedbackCeiling_CapsInfluence
    //   Arrange: learning with FeedbackWeight=5.0, freshness=1.0, alpha=0.25, ceiling=0.3.
    //   Act: invoke handler.
    //   Assert: feedback contribution is capped at 0.3, not 5.0 * 0.25.

    // Test: Handle_DiversityInjection_IncludesNonOptimized
    //   Arrange: 10 learnings, DiversityInjectionRatio=0.15 → 1.5 → 1 slot replaced.
    //     Bottom 1 result replaced by a random non-top-scored learning.
    //   Act: invoke with maxResults=10.
    //   Assert: result count is 10. At least one result was not in the top-9 by FinalScore
    //     (verify by checking that the set differs from pure-sorted top-10).

    // Test: Handle_DiversityInjection_SkippedWhenTooFewResults
    //   Arrange: only 1 learning returned from store.
    //   Act: invoke handler.
    //   Assert: result contains exactly 1 learning (no diversity injection with < 2 results).

    // Test: Handle_FiresRecordLearningAccessCommand
    //   Arrange: store returns 2 learnings.
    //   Act: invoke handler.
    //   Assert: IMediator.Send called with RecordLearningAccessCommand containing 2 LearningIds.

    // Test: Handle_EmptyResults_ReturnsEmptyList
    //   Arrange: store returns empty list.
    //   Act: invoke handler.
    //   Assert: result.IsSuccess is true, result.Value is empty.

    // Test: Handle_UsesEmbeddingServiceForRelevance
    //   Arrange: mock IEmbeddingService.EmbedQueryAsync to return known vector.
    //   Act: invoke handler.
    //   Assert: EmbedQueryAsync called once with the query context string.

    // Test: Handle_Disabled_ReturnsSuccessEmptyList
    //   Arrange: LearningsConfig.Enabled = false.
    //   Act: invoke handler.
    //   Assert: result.IsSuccess, result.Value is empty list, store NOT queried.
}
```

### ForgetCommandHandlerTests

File: `src/Content/Tests/Application.Core.Tests/CQRS/Learnings/ForgetCommandHandlerTests.cs`

```csharp
namespace Application.Core.Tests.CQRS.Learnings;

/// <summary>
/// Tests for <see cref="ForgetCommandHandler"/>.
/// </summary>
public sealed class ForgetCommandHandlerTests
{
    // Test: Handle_ValidId_SoftDeletesLearning
    //   Arrange: store.SoftDeleteAsync returns success.
    //   Act: invoke handler with valid learningId and reason.
    //   Assert: SoftDeleteAsync called once with correct learningId and reason.

    // Test: Handle_NotFound_ReturnsNotFound
    //   Arrange: store.SoftDeleteAsync returns NotFound result.
    //   Act: invoke handler.
    //   Assert: result.IsSuccess is false, result.FailureType is NotFound.

    // Test: Handle_RequiresReason
    //   (Validation is handled by the pipeline behavior / validator from section-06.
    //    This test verifies the handler doesn't bypass validation.)

    // Test: Handle_Disabled_ReturnsSuccessNoOp
    //   Arrange: LearningsConfig.Enabled = false.
    //   Act: invoke handler.
    //   Assert: result.IsSuccess is true, store NOT called.
}
```

### ImproveLearningCommandHandlerTests

File: `src/Content/Tests/Application.Core.Tests/CQRS/Learnings/ImproveLearningCommandHandlerTests.cs`

```csharp
namespace Application.Core.Tests.CQRS.Learnings;

/// <summary>
/// Tests for <see cref="ImproveLearningCommandHandler"/>.
/// </summary>
public sealed class ImproveLearningCommandHandlerTests
{
    // Test: Handle_AppliesEmaToFeedbackWeight
    //   Arrange: existing learning with FeedbackWeight=1.0, config alpha=0.25.
    //     FeedbackScore=4.0 → normalized to 0.75 (score-1)/4.
    //   Act: invoke handler.
    //   Assert: new weight = 0.25 * 0.75 + 0.75 * 1.0 = 0.9375 (approximately).

    // Test: Handle_EmaCalculation_KnownInputs
    //   Arrange: learning with FeedbackWeight=0.6, UpdateCount=3, alpha=0.25, score=5.0 (normalized=1.0).
    //   Act: invoke handler.
    //   Assert: new weight = 0.25 * 1.0 + 0.75 * 0.6 = 0.7 exactly.

    // Test: Handle_BiasCorrection_NewLearning
    //   Arrange: UpdateCount=1, BiasCorrection=true, alpha=0.25.
    //   Act: invoke handler.
    //   Assert: raw EMA weight adjusted by 1/(1-(1-alpha)^updateCount) correction factor.

    // Test: Handle_BiasCorrection_Disabled_NoAdjustment
    //   Arrange: UpdateCount=1, BiasCorrection=false.
    //   Act: invoke handler.
    //   Assert: weight uses raw EMA without bias correction.

    // Test: Handle_IncrementsUpdateCount
    //   Arrange: existing learning with UpdateCount=2.
    //   Act: invoke handler.
    //   Assert: updated learning has UpdateCount=3.

    // Test: Handle_SetsLastReinforcedAt
    //   Arrange: FakeTimeProvider at specific time.
    //   Act: invoke handler.
    //   Assert: updated learning.LastReinforcedAt matches FakeTimeProvider time.

    // Test: Handle_AboveThreshold_SignalsBaselineAdjustment
    //   Arrange: after EMA update, FeedbackWeight >= BaselineAdjustmentThreshold (0.8).
    //     Learning source is DriftDetection.
    //   Act: invoke handler.
    //   Assert: handler signals that baseline adjustment is needed
    //     (implementation detail: may set a flag on the result, or dispatch a domain event,
    //      or directly invoke IDriftDetectionService — section-17 bridge handles this).

    // Test: Handle_BelowThreshold_NoBaselineSignal
    //   Arrange: after EMA update, FeedbackWeight = 0.5 (below 0.8 threshold).
    //   Act: invoke handler.
    //   Assert: no baseline adjustment signaled.

    // Test: Handle_InvalidScore_ReturnsValidationFailure
    //   (Validation handled by pipeline behavior. Handler tests can verify behavior when
    //    store returns unexpected states.)

    // Test: Handle_LearningNotFound_ReturnsNotFound
    //   Arrange: store.GetAsync returns null.
    //   Act: invoke handler.
    //   Assert: result.IsSuccess is false, FailureType is NotFound.

    // Test: Handle_Disabled_ReturnsSuccessNoOp
    //   Arrange: LearningsConfig.Enabled = false.
    //   Act: invoke handler.
    //   Assert: result.IsSuccess, store NOT called.
}
```

### RecordLearningAccessCommandHandlerTests

File: `src/Content/Tests/Application.Core.Tests/CQRS/Learnings/RecordLearningAccessCommandHandlerTests.cs`

```csharp
namespace Application.Core.Tests.CQRS.Learnings;

/// <summary>
/// Tests for <see cref="RecordLearningAccessCommandHandler"/>.
/// </summary>
public sealed class RecordLearningAccessCommandHandlerTests
{
    // Test: Handle_UpdatesLastAccessedAt
    //   Arrange: store returns a learning for each ID in the command.
    //   Act: invoke handler.
    //   Assert: store.UpdateAsync called for each learning with LastAccessedAt set to command.AccessedAt.

    // Test: Handle_MissingLearning_SkipsWithoutError
    //   Arrange: one ID in the list, store returns null for it.
    //   Act: invoke handler.
    //   Assert: result.IsSuccess is true (fire-and-forget, doesn't fail on missing entries).

    // Test: Handle_EmptyIdList_ReturnsSuccess
    //   Arrange: command with empty LearningIds list.
    //   Act: invoke handler.
    //   Assert: result.IsSuccess, no store calls.
}
```

### LearningsMetricsTests

File: `src/Content/Tests/Application.Core.Tests/CQRS/Learnings/LearningsMetricsTests.cs`

```csharp
namespace Application.Core.Tests.CQRS.Learnings;

/// <summary>
/// Smoke tests for <see cref="LearningsMetrics"/> instrument creation.
/// Verifies static instruments are non-null and have correct names.
/// </summary>
public sealed class LearningsMetricsTests
{
    // Test: Remembered_Counter_IsNotNull
    // Test: Recalled_Counter_IsNotNull
    // Test: Forgotten_Counter_IsNotNull
    // Test: Improved_Counter_IsNotNull
    // Test: Pruned_Counter_IsNotNull
    // Test: RecallDurationMs_Histogram_IsNotNull
}
```

---

## Implementation Details

### RememberCommandHandler

File: `src/Content/Application/Application.Core/CQRS/Learnings/RememberCommandHandler.cs`

Implements `IRequestHandler<RememberCommand, Result<LearningEntry>>`.

**Constructor dependencies:**
- `ILearningsStore` (resolved from keyed DI via default key)
- `ILearningNotificationChannel` (for notifications)
- `IOptionsMonitor<AppConfig>` (access `AppConfig.AI.Learnings`)
- `TimeProvider`
- `ILogger<RememberCommandHandler>`

**Handle flow:**
1. Check `LearningsConfig.Enabled`. If false, return `Result<LearningEntry>.Success(default LearningEntry with content)` or a no-op success. Prefer returning early with a Result that signals no-op clearly.
2. Determine `DecayClass`: if command has explicit `DecayClass`, use it. Otherwise map from `LearningCategory`:
   - `FactualCorrection` -> `Permanent`
   - `DomainKnowledge` -> `Permanent`
   - `InstructionUpdate` -> `Stable`
   - `StylePreference` -> `Stable`
   - `ToolUsagePattern` -> `Stable`
3. Create `LearningEntry` record with:
   - `LearningId` = `Guid.NewGuid()`
   - All fields from command
   - `FeedbackWeight` = 1.0, `UpdateCount` = 0
   - `CreatedAt` = `TimeProvider.GetUtcNow()`
   - `LastAccessedAt` = same as `CreatedAt`
   - `LastReinforcedAt` = same as `CreatedAt`
4. Call `ILearningsStore.SaveAsync(entry, ct)`. If failure, return that failure.
5. Fire `ILearningNotificationChannel.NotifyLearningCapturedAsync(entry, ct)` — fire-and-forget, catch and log exceptions.
6. Increment `LearningsMetrics.Remembered` counter, tagged with `category` and `scope`.
7. Return `Result<LearningEntry>.Success(entry)`.

### RecallQueryHandler

File: `src/Content/Application/Application.Core/CQRS/Learnings/RecallQueryHandler.cs`

Implements `IRequestHandler<RecallQuery, Result<IReadOnlyList<WeightedLearning>>>`.

**Constructor dependencies:**
- `ILearningsStore`
- `ILearningDecayService`
- `IEmbeddingService` (from `Application.AI.Common.Interfaces.RAG`)
- `IMediator` (for fire-and-forget `RecordLearningAccessCommand`)
- `IOptionsMonitor<AppConfig>`
- `TimeProvider`
- `ILogger<RecallQueryHandler>`

**Handle flow:**

This is the most complex handler. It blends semantic relevance with feedback and freshness.

1. Check `Enabled`. If false, return `Result<IReadOnlyList<WeightedLearning>>.Success(Array.Empty<WeightedLearning>())`.
2. Build `LearningSearchCriteria` from the query's `Scope`. The store handles scope hierarchy merging (agent -> team -> global) internally.
3. Call `ILearningsStore.SearchAsync(criteria, ct)` to get candidate learnings.
4. If empty, return success with empty list.
5. Embed the query context: `var queryEmbedding = await _embeddingService.EmbedQueryAsync(query.Context, ct)`.
6. For each candidate learning, compute scores:
   - **`relevanceScore`**: Cosine similarity between `queryEmbedding` and the learning's content embedding. If the learning doesn't have an embedding, embed it on-the-fly or assign a low relevance.
   - **`feedbackScore`**: The learning's `FeedbackWeight` (already EMA-weighted from `ImproveLearningCommand` updates).
   - **`freshnessScore`**: `await _decayService.CalculateFreshnessAsync(learning, ct)`.
   - **`finalScore`**: `(1 - alpha) * relevanceScore + alpha * Math.Min(feedbackScore * freshnessScore, ceiling)` where `alpha = config.FeedbackAlpha`, `ceiling = config.FeedbackCeiling`.
7. Build `WeightedLearning` records for each candidate.
8. Sort by `FinalScore` descending.
9. Apply diversity injection:
   - If total results >= 2 and `DiversityInjectionRatio > 0`:
     - `slotsToReplace = (int)Math.Floor(maxResults * config.DiversityInjectionRatio)`
     - If `slotsToReplace >= 1`: take the top `maxResults - slotsToReplace` by score, then fill remaining slots with random learnings NOT in the top set.
   - Otherwise, skip diversity injection.
10. Take top `query.MaxResults` from the final list. Filter by `query.MinRelevance` if > 0.
11. Fire-and-forget: `_ = _mediator.Send(new RecordLearningAccessCommand { LearningIds = results.Select(r => r.Learning.LearningId).ToList(), AccessedAt = _timeProvider.GetUtcNow() }, ct)`. Don't await — this is CQRS-clean side-effect tracking.
12. Record `LearningsMetrics.Recalled` counter and `LearningsMetrics.RecallDurationMs` histogram.
13. Return `Result<IReadOnlyList<WeightedLearning>>.Success(results)`.

**Cosine similarity computation**: Either use a helper method directly or leverage the existing RAG infrastructure. The cosine similarity calculation is `dot(a, b) / (norm(a) * norm(b))`. Since `IEmbeddingService` returns `ReadOnlyMemory<float>`, implement a private static helper:

```csharp
/// <summary>Computes cosine similarity between two embedding vectors.</summary>
private static double CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
{
    // dot product / (magnitude_a * magnitude_b)
    // Return 0.0 if either vector has zero magnitude.
}
```

### ForgetCommandHandler

File: `src/Content/Application/Application.Core/CQRS/Learnings/ForgetCommandHandler.cs`

Implements `IRequestHandler<ForgetCommand, Result>`.

**Constructor dependencies:**
- `ILearningsStore`
- `IOptionsMonitor<AppConfig>`
- `ILogger<ForgetCommandHandler>`

**Handle flow:**
1. Check `Enabled`. If false, return `Result.Success()`.
2. Call `ILearningsStore.SoftDeleteAsync(command.LearningId, command.Reason, ct)`.
3. If store returns failure (e.g., not found), return that failure.
4. Increment `LearningsMetrics.Forgotten` counter.
5. Return `Result.Success()`.

### ImproveLearningCommandHandler

File: `src/Content/Application/Application.Core/CQRS/Learnings/ImproveLearningCommandHandler.cs`

Implements `IRequestHandler<ImproveLearningCommand, Result<LearningEntry>>`.

**Constructor dependencies:**
- `ILearningsStore`
- `IOptionsMonitor<AppConfig>`
- `TimeProvider`
- `ILogger<ImproveLearningCommandHandler>`

**Handle flow:**
1. Check `Enabled`. If false, return no-op success.
2. Retrieve learning: `var result = await _store.GetAsync(command.LearningId, ct)`. If null/not-found, return `Result<LearningEntry>.NotFound(...)`.
3. Normalize feedback score to [0, 1]: `normalized = (command.FeedbackScore - 1.0) / 4.0` (maps 1.0-5.0 to 0.0-1.0).
4. Apply EMA: `newWeight = alpha * normalized + (1 - alpha) * learning.FeedbackWeight` where `alpha = config.FeedbackAlpha`.
5. Bias correction (if `config.BiasCorrection` and `learning.UpdateCount < 5`):
   - `correctionFactor = 1.0 / (1.0 - Math.Pow(1.0 - alpha, learning.UpdateCount + 1))`
   - `newWeight = Math.Clamp(newWeight * correctionFactor, 0.0, 1.0)`
6. Create updated learning record (using `with` expression on the record):
   - `FeedbackWeight = newWeight`
   - `UpdateCount = learning.UpdateCount + 1`
   - `LastReinforcedAt = _timeProvider.GetUtcNow()`
   - If `command.ReinforcementContent` is provided, append or update Content as appropriate.
7. Save via `_store.UpdateAsync(updated, ct)`.
8. Check baseline adjustment threshold: if `newWeight >= config.BaselineAdjustmentThreshold` and learning's `Source.SourceType == LearningSourceType.DriftDetection`, set a flag or property on the result signaling that baseline adjustment should be triggered. The section-17 bridge will consume this signal.
   - Implementation approach: return the updated `LearningEntry` and let section-17's bridge check the weight threshold independently. This keeps the handler focused on the EMA update.
9. Increment `LearningsMetrics.Improved` counter.
10. Return `Result<LearningEntry>.Success(updated)`.

### RecordLearningAccessCommandHandler

File: `src/Content/Application/Application.Core/CQRS/Learnings/RecordLearningAccessCommandHandler.cs`

Implements `IRequestHandler<RecordLearningAccessCommand, Result>`.

**Constructor dependencies:**
- `ILearningsStore`
- `ILogger<RecordLearningAccessCommandHandler>`

**Handle flow:**
1. For each `learningId` in `command.LearningIds`:
   - `var result = await _store.GetAsync(learningId, ct)`.
   - If found, update `LastAccessedAt = command.AccessedAt` via `_store.UpdateAsync(updated, ct)`.
   - If not found, log at Debug level and skip.
2. Return `Result.Success()`.

This handler is intentionally lenient — it's a fire-and-forget side effect of recall. Missing learnings (deleted between recall and access recording) are silently skipped.

---

## LearningConventions

File: `src/Content/Domain/Domain.AI/Telemetry/Conventions/LearningConventions.cs`

Static class following the `EscalationConventions` pattern. Defines OTel attribute name constants and metric identifier constants.

```csharp
public static class LearningConventions
{
    // Attribute name constants
    public const string LearningId = "agent.learning.id";
    public const string AgentId = "agent.learning.agent_id";
    public const string Category = "agent.learning.category";
    public const string DecayClass = "agent.learning.decay_class";
    public const string Scope = "agent.learning.scope";

    // Metric identifier constants
    public const string Remembered = "agent.learning.remembered";
    public const string Recalled = "agent.learning.recalled";
    public const string Forgotten = "agent.learning.forgotten";
    public const string Improved = "agent.learning.improved";
    public const string Pruned = "agent.learning.pruned";
    public const string RecallDurationMs = "agent.learning.recall_duration_ms";

    // Well-known tag value classes
    public static class CategoryValues { /* one const per LearningCategory enum value */ }
    public static class DecayClassValues { /* one const per DecayClass enum value */ }
    public static class ScopeValues { /* "agent", "team", "global" */ }
}
```

---

## LearningsMetrics

File: `src/Content/Application/Application.AI.Common/OpenTelemetry/Metrics/LearningsMetrics.cs`

Static class following the `EscalationMetrics` pattern.

```csharp
/// <summary>
/// OTel metric instruments for the learnings subsystem.
/// Recorded by command handlers on remember, recall, forget, improve, and prune operations.
/// </summary>
public static class LearningsMetrics
{
    /// <summary>Learnings captured. Tags: category, scope.</summary>
    public static Counter<long> Remembered { get; } =
        AppInstrument.Meter.CreateCounter<long>(LearningConventions.Remembered, "{learning}", "Learnings remembered");

    /// <summary>Learnings recalled. Tags: (none standard).</summary>
    public static Counter<long> Recalled { get; } =
        AppInstrument.Meter.CreateCounter<long>(LearningConventions.Recalled, "{learning}", "Learnings recalled");

    /// <summary>Learnings soft-deleted.</summary>
    public static Counter<long> Forgotten { get; } =
        AppInstrument.Meter.CreateCounter<long>(LearningConventions.Forgotten, "{learning}", "Learnings forgotten");

    /// <summary>Learnings feedback-improved.</summary>
    public static Counter<long> Improved { get; } =
        AppInstrument.Meter.CreateCounter<long>(LearningConventions.Improved, "{learning}", "Learnings improved");

    /// <summary>Expired learnings pruned by background service.</summary>
    public static Counter<long> Pruned { get; } =
        AppInstrument.Meter.CreateCounter<long>(LearningConventions.Pruned, "{learning}", "Learnings pruned");

    /// <summary>Recall pipeline duration histogram.</summary>
    public static Histogram<double> RecallDurationMs { get; } =
        AppInstrument.Meter.CreateHistogram<double>(LearningConventions.RecallDurationMs, "ms", "Recall query duration");
}
```

---

## Implementation Checklist

1. Create `LearningConventions.cs` in `Domain.AI/Telemetry/Conventions/`
2. Create `LearningsMetrics.cs` in `Application.AI.Common/OpenTelemetry/Metrics/`
3. Create all test files in `Application.Core.Tests/CQRS/Learnings/`
4. Create `RememberCommandHandler.cs`
5. Create `RecallQueryHandler.cs` (most complex — embedding + scoring + diversity)
6. Create `ForgetCommandHandler.cs`
7. Create `ImproveLearningCommandHandler.cs`
8. Create `RecordLearningAccessCommandHandler.cs`
9. Verify build: `dotnet build src/AgenticHarness.slnx`
10. Run tests: `dotnet test src/AgenticHarness.slnx`

## Conventions

- Handlers auto-discovered by MediatR assembly scanning (existing behavior from `Application.Core` DI).
- All handlers inject `IOptionsMonitor<AppConfig>` and check `config.AI.Learnings.Enabled` as the first operation.
- All handlers inject `ILogger<T>` and log at appropriate levels (Information for successful operations, Warning for unexpected states, Debug for fire-and-forget skips).
- Result pattern: `Result<T>.Success(value)` for success, `Result<T>.NotFound(reason)` for missing entities, `Result<T>.Fail(reason)` for general failures.
- Records updated via `with` expressions to preserve immutability.
- `TimeProvider` injected everywhere — never use `DateTimeOffset.UtcNow` directly.
- Namespace: `Application.Core.CQRS.Learnings` for handlers, `Application.AI.Common.OpenTelemetry.Metrics` for metrics, `Domain.AI.Telemetry.Conventions` for conventions.
- XML docs on all public types and methods — this is a template, docs are teaching material.

---

## Actual Implementation Notes

**Status:** Complete. Build green, 59 tests pass (34 new + 25 pre-existing).

### Deviations from Plan

| # | Planned | Actual | Rationale |
|---|---------|--------|-----------|
| 1 | Sequential embedding in RecallQueryHandler (foreach loop) | Parallel embedding via `Task.WhenAll` | Code review fix — O(n) sequential API calls replaced with parallel for performance |
| 2 | MinRelevance filter after diversity injection (step 10) | MinRelevance filter BEFORE diversity injection | Code review fix — filtering after diversity could remove injected diversity picks that were below threshold, defeating the purpose |
| 3 | Bare `_ = _mediator.Send(...)` fire-and-forget | Wrapped in `RecordAccessSafeAsync` helper with try/catch | Code review fix — unhandled exceptions in fire-and-forget would crash the process |
| 4 | ImproveLearningCommandHandler disabled returns `default!` | Returns `CreateDisabledPlaceholder(request.LearningId)` with valid `LearningEntry` | Code review fix — null return violated caller expectations; matches RememberHandler pattern |
| 5 | RecordLearningAccessCommandHandler ignores UpdateAsync result | Logs UpdateAsync failures at Warning level | Code review fix — silent failures hid store errors |
| 6 | Baseline adjustment threshold signaling in ImproveLearningCommandHandler (step 8) | Deferred — handler returns updated entry, section-17 bridge checks threshold | Keeps handler focused on EMA; bridge owns cross-concern coordination |
| 7 | `CosineSimilarity` and `ApplyDiversityInjection` as `private static` | Made `internal static` | Enables direct unit testing without going through full handler pipeline |

### Additional Changes Not in Plan

- `Application.Core.Tests.csproj`: Added `<PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" />` for `FakeTimeProvider`
- All test files use a shared helper pattern: `CreateOptions(LearningsConfig)` wrapping `Mock<IOptionsMonitor<AppConfig>>`

### Test Coverage

| Test File | Test Count |
|-----------|-----------|
| RememberCommandHandlerTests | 7 |
| RecallQueryHandlerTests | 7 |
| ForgetCommandHandlerTests | 3 |
| ImproveLearningCommandHandlerTests | 8 |
| RecordLearningAccessCommandHandlerTests | 3 |
| LearningsMetricsTests | 6 |
| **Total new tests** | **34** |

### Code Review Trail

- Review: `planning/phase3-quality-loop/implementation/code_review/section-13-review.md`
- Interview: `planning/phase3-quality-loop/implementation/code_review/section-13-interview.md`
