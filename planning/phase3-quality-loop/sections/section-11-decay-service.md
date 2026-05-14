# Section 11: Learning Decay Service

## Status: IMPLEMENTED

## Overview

This section implements `DefaultLearningDecayService` and `LearningsPruningBackgroundService` in the Infrastructure.AI layer. The decay service calculates temporal freshness scores for learnings based on their `DecayClass` (Volatile, Stable, Permanent) and optionally applies EMA bias correction for new learnings using a dedicated `DecayBiasAlpha` config. The pruning background service runs on a configurable interval to soft-delete expired learnings across all scopes.

**Layer:** Infrastructure.AI

## Deviations from Plan
1. **`DecayBiasAlpha` added to `LearningsConfig`** — Separate config for decay bias correction instead of reusing `FeedbackAlpha` (different mathematical contexts).
2. **`LearningSearchCriteria.Scope` made nullable** — Pruning searches all scopes (null = no scope filter) instead of hardcoded IsGlobal=true.
3. **`LearningsConfigValidator` updated** — Added validation for `DecayBiasAlpha` (0, 1].
4. **Background service uses `Task.Delay` loop** — Not `PeriodicTimer` with `TimeProvider` as spec suggested. `PeriodicTimer` + `FakeTimeProvider` was unreliable in tests. Follows existing `RetentionEnforcementService` pattern.
5. **Defensive guards added** — Division by zero for zero shelf life, `Math.Clamp` for future timestamps, `ArgumentNullException.ThrowIfNull` on constructors, SoftDeleteAsync result checking.
6. **`PruneNowAsync` exposed** — Public method on background service for testability.
7. **25 tests** — 19 from spec + 6 edge cases (zero shelf life, future timestamp, exact boundary, UpdateCount 0/5, null scope search).

**Dependencies (must be implemented first):**
- Section 02 — Learnings domain models (`LearningEntry`, `DecayClass`, `LearningScope`, etc.) in `Domain.AI/Learnings/`
- Section 04 — `LearningsConfig` in `Domain.Common/Config/AI/` with shelf life, bias correction, and prune interval settings
- Section 06 — `ILearningDecayService` and `ILearningsStore` interfaces in `Application.AI.Common/Interfaces/Learnings/`

**Blocks:** Section 13 (MediatR command handlers use `ILearningDecayService.CalculateFreshnessAsync` in the recall pipeline)

---

## Files to Create

| File | Purpose |
|------|---------|
| `src/Content/Infrastructure/Infrastructure.AI/Learnings/DefaultLearningDecayService.cs` | `ILearningDecayService` implementation |
| `src/Content/Infrastructure/Infrastructure.AI/Learnings/LearningsPruningBackgroundService.cs` | `BackgroundService` for periodic pruning |
| `src/Content/Tests/Infrastructure.AI.Tests/Learnings/DefaultLearningDecayServiceTests.cs` | Tests for freshness calculation and pruning |
| `src/Content/Tests/Infrastructure.AI.Tests/Learnings/LearningsPruningBackgroundServiceTests.cs` | Tests for the background service lifecycle |

---

## Tests (Write First)

### DefaultLearningDecayServiceTests

Test file: `src/Content/Tests/Infrastructure.AI.Tests/Learnings/DefaultLearningDecayServiceTests.cs`

Test class setup: inject `FakeTimeProvider`, `Mock<ILearningsStore>`, `Mock<IOptionsMonitor<LearningsConfig>>` (or `IOptionsMonitor<AppConfig>` depending on DI choice — the project uses both patterns; follow the `IOptionsMonitor<EscalationConfig>` precedent and inject the sub-config directly as `IOptionsMonitor<LearningsConfig>`).

```csharp
// Test: CalculateFreshness_VolatileDecay_7DayShelfLife
//   Arrange: LearningEntry with DecayClass.Volatile, CreatedAt = now - 3.5 days, never reinforced
//   Config: VolatileShelfLifeDays = 7
//   Assert: freshness == 0.5 (halfway through shelf life)

// Test: CalculateFreshness_StableDecay_180DayShelfLife
//   Arrange: LearningEntry with DecayClass.Stable, CreatedAt = now - 90 days, never reinforced
//   Config: StableShelfLifeDays = 180
//   Assert: freshness == 0.5

// Test: CalculateFreshness_PermanentDecay_AlwaysReturnsOne
//   Arrange: LearningEntry with DecayClass.Permanent, CreatedAt = now - 365 days
//   Assert: freshness == 1.0 (permanent learnings never decay)

// Test: CalculateFreshness_Expired_ReturnsZero
//   Arrange: LearningEntry with DecayClass.Volatile, CreatedAt = now - 10 days
//   Config: VolatileShelfLifeDays = 7
//   Assert: freshness == 0.0 (max(0, 1 - 10/7) = 0)

// Test: CalculateFreshness_Halfway_ReturnsPointFive
//   Arrange: LearningEntry with DecayClass.Stable, CreatedAt = now - 90 days
//   Config: StableShelfLifeDays = 180
//   Assert: freshness == 0.5

// Test: CalculateFreshness_UsesLastReinforcedAt_WhenAvailable
//   Arrange: LearningEntry with CreatedAt = now - 100 days, LastReinforcedAt = now - 2 days
//   Config: VolatileShelfLifeDays = 7, DecayClass.Volatile
//   Assert: freshness calculated from LastReinforcedAt (age = 2 days), not CreatedAt
//   Expected: max(0, 1 - 2/7) ≈ 0.714

// Test: CalculateFreshness_FallsBackToCreatedAt_WhenNeverReinforced
//   Arrange: LearningEntry with CreatedAt = now - 5 days, LastReinforcedAt = CreatedAt (or sentinel indicating never reinforced)
//   Assert: freshness calculated from CreatedAt

// Test: CalculateFreshness_BiasCorrection_NewLearning_AdjustsUp
//   Arrange: LearningEntry with DecayClass.Stable, UpdateCount = 1, age = 90 days
//   Config: BiasCorrection = true, FeedbackAlpha = 0.25
//   Expected: raw freshness = 0.5, then correction factor = 1 / (1 - (1 - 0.25)^1) = 1/0.25 = 4.0
//   Clamped to [0, 1] -> 1.0 (correction lifts low-sample learnings)

// Test: CalculateFreshness_BiasCorrection_Disabled_NoAdjustment
//   Arrange: same as above but BiasCorrection = false
//   Assert: freshness == 0.5 (no correction applied)

// Test: PruneExpired_RemovesExpiredVolatile
//   Arrange: store returns 3 volatile learnings — 2 expired (freshness <= 0), 1 fresh
//   Assert: SoftDeleteAsync called twice, returns count 2

// Test: PruneExpired_KeepsPermanent
//   Arrange: store returns 2 permanent learnings (even very old ones)
//   Assert: SoftDeleteAsync never called, returns count 0

// Test: PruneExpired_KeepsFreshStable
//   Arrange: store returns stable learning with age < shelf life
//   Assert: SoftDeleteAsync never called, returns count 0

// Test: PruneExpired_ReturnsCount
//   Arrange: store returns 5 learnings, 3 expired
//   Assert: result.Value == 3
```

### LearningsPruningBackgroundServiceTests

Test file: `src/Content/Tests/Infrastructure.AI.Tests/Learnings/LearningsPruningBackgroundServiceTests.cs`

```csharp
// Test: PruningBackgroundService_RunsOnInterval
//   Arrange: FakeTimeProvider, Mock<ILearningDecayService>, PruneIntervalHours = 1
//   Act: Start service, advance time by 1 hour
//   Assert: PruneExpiredAsync called once

// Test: PruningBackgroundService_UsesTimeProvider
//   Arrange: FakeTimeProvider set to specific time
//   Act: Advance by interval
//   Assert: PeriodicTimer fires correctly (verify PruneExpiredAsync invoked at expected times)

// Test: PruningBackgroundService_StopsOnCancellation
//   Arrange: CancellationTokenSource
//   Act: Start service, cancel token
//   Assert: Service exits cleanly without throwing
```

---

## Implementation Details

### DefaultLearningDecayService

**File:** `src/Content/Infrastructure/Infrastructure.AI/Learnings/DefaultLearningDecayService.cs`

**Namespace:** `Infrastructure.AI.Learnings`

**Constructor dependencies:**
- `ILearningsStore` — to query and soft-delete learnings during pruning
- `IOptionsMonitor<LearningsConfig>` — shelf life values, bias correction flag, feedback alpha
- `TimeProvider` — for deterministic `now` in freshness calculations
- `ILogger<DefaultLearningDecayService>` — structured logging

**`CalculateFreshnessAsync(LearningEntry learning, CancellationToken ct)` -> `double`:**

1. If `learning.DecayClass == DecayClass.Permanent`, return `1.0` immediately
2. Resolve shelf life from config:
   - `DecayClass.Volatile` -> `config.VolatileShelfLifeDays`
   - `DecayClass.Stable` -> `config.StableShelfLifeDays`
3. Determine reference time: use `learning.LastReinforcedAt` if it differs from `learning.CreatedAt` (indicating reinforcement occurred); otherwise fall back to `learning.CreatedAt`
4. Calculate age: `TimeProvider.GetUtcNow() - referenceTime`
5. Calculate raw freshness: `Math.Max(0.0, 1.0 - (age.TotalDays / shelfLifeDays))`
6. If `config.BiasCorrection == true` AND `learning.UpdateCount < 5` AND `learning.UpdateCount > 0`:
   - Correction factor: `1.0 / (1.0 - Math.Pow(1.0 - config.FeedbackAlpha, learning.UpdateCount))`
   - Apply: `correctedFreshness = Math.Clamp(rawFreshness * correctionFactor, 0.0, 1.0)`
   - Return corrected value
7. Otherwise return raw freshness

The bias correction formula `1 / (1 - (1-alpha)^n)` is the standard EMA bias correction used to counteract the under-weighting of early observations. For `n=1, alpha=0.25` this gives `1/0.25 = 4.0`, which when multiplied by a low freshness score and clamped to [0,1], effectively boosts new learnings that haven't had enough feedback cycles to stabilize their weight.

**`PruneExpiredAsync(CancellationToken ct)` -> `Result<int>`:**

1. Query all non-permanent learnings via `ILearningsStore.SearchAsync` with a criteria that filters out `DecayClass.Permanent`
2. For each learning, call `CalculateFreshnessAsync`
3. If freshness <= 0, call `ILearningsStore.SoftDeleteAsync(learning.LearningId, "Expired by decay service")`
4. Count pruned entries
5. Log at Information level: `"Pruned {Count} expired learnings"`
6. Return `Result<int>.Success(prunedCount)`

### LearningsPruningBackgroundService

**File:** `src/Content/Infrastructure/Infrastructure.AI/Learnings/LearningsPruningBackgroundService.cs`

**Namespace:** `Infrastructure.AI.Learnings`

**Pattern:** Follow the `LlmRetryQueue` and `RetentionEnforcementService` BackgroundService patterns already in the codebase.

**Constructor dependencies:**
- `ILearningDecayService` — the service to invoke for pruning
- `IOptionsMonitor<LearningsConfig>` — for `PruneIntervalHours`
- `TimeProvider` — for `PeriodicTimer` construction (testable via `FakeTimeProvider`)
- `ILogger<LearningsPruningBackgroundService>` — structured logging

**`ExecuteAsync(CancellationToken stoppingToken)`:**

1. Create `PeriodicTimer` using `TimeProvider` with interval `TimeSpan.FromHours(config.PruneIntervalHours)`
2. Loop while `await timer.WaitForNextTickAsync(stoppingToken)` returns true:
   - Try: call `ILearningDecayService.PruneExpiredAsync(stoppingToken)`
   - Log the pruned count at Information level
   - Catch `OperationCanceledException` when `stoppingToken.IsCancellationRequested` -> break
   - Catch `Exception` -> log at Error level, continue (don't crash the background service)

**DI registration note (for Section 18):** This service is registered conditionally:
```csharp
if (learningsConfig.Enabled)
    services.AddHostedService<LearningsPruningBackgroundService>();
```

### Key Design Decisions

1. **`IOptionsMonitor<LearningsConfig>` not `IOptionsMonitor<AppConfig>`**: The decay service only needs learnings config. This follows the `DefaultEscalationService` pattern which injects `IOptionsMonitor<EscalationConfig>` directly.

2. **`PeriodicTimer` with `TimeProvider`**: This is the .NET 8+ testable timer pattern. `FakeTimeProvider.Advance()` triggers the timer tick in tests without real delays.

3. **Soft-delete, not hard-delete**: Expired learnings are soft-deleted (flag set on graph node) so they remain available for audit. The `ILearningsStore.SoftDeleteAsync` method (Section 12) handles this.

4. **Bias correction clamped to [0, 1]**: The correction factor can exceed 1.0 for very new learnings (UpdateCount=1 gives factor 4.0). Clamping prevents freshness from exceeding 1.0.

5. **UpdateCount > 0 guard**: Bias correction only applies when the learning has been improved at least once. A brand-new learning (UpdateCount=0) has no feedback history to correct for.

---

## Domain Types Referenced (from Sections 02, 04, 06)

These types must exist before implementing this section:

- **`LearningEntry`** (Section 02): record with `LearningId`, `DecayClass`, `CreatedAt`, `LastReinforcedAt`, `UpdateCount`, `FeedbackWeight`, and other fields
- **`DecayClass`** (Section 02): enum with `Volatile`, `Stable`, `Permanent`
- **`LearningsConfig`** (Section 04): class with `Enabled`, `VolatileShelfLifeDays` (default 7), `StableShelfLifeDays` (default 180), `PruneIntervalHours` (default 24), `FeedbackAlpha` (default 0.25), `BiasCorrection` (default true)
- **`ILearningDecayService`** (Section 06): interface with `CalculateFreshnessAsync(LearningEntry, CancellationToken)` -> `double` and `PruneExpiredAsync(CancellationToken)` -> `Result<int>`
- **`ILearningsStore`** (Section 06): interface with `SearchAsync(LearningSearchCriteria, CancellationToken)` -> `Result<IReadOnlyList<LearningEntry>>` and `SoftDeleteAsync(Guid, string, CancellationToken)` -> `Result`
- **`LearningSearchCriteria`** (Section 06): DTO for filtering learnings by scope, category, etc.

---

## Mathematical Reference

**Freshness formula (linear decay):**
```
freshness = max(0, 1 - age_days / shelf_life_days)
```

**Bias correction factor (EMA warm-up):**
```
correction = 1 / (1 - (1 - alpha)^n)
```
where `alpha` = `FeedbackAlpha`, `n` = `UpdateCount`

Example values:
| UpdateCount | Alpha=0.25 | Correction Factor |
|------------|------------|-------------------|
| 1          | 0.25       | 4.000             |
| 2          | 0.25       | 2.286             |
| 3          | 0.25       | 1.753             |
| 4          | 0.25       | 1.508             |
| 5+         | 0.25       | Not applied        |

The correction compensates for the fact that EMA-weighted feedback scores converge slowly with few observations. After 5 updates, the EMA is considered sufficiently stable and no correction is needed.
