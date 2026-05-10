# Section 15: Retry Queue -- LlmRetryQueue

## Overview

This section implements `LlmRetryQueue`, a `BackgroundService` that provides graceful degradation when all LLM providers in the fallback chain are exhausted. Instead of simply failing, the system queues the request and automatically retries when any provider recovers (detected via `IProviderHealthMonitor.OnCircuitStateChanged`).

The queue is in-memory with configurable max size and TTL. It is **conditionally registered** -- only when `ResilienceConfig.Enabled == true`. A periodic sweep removes expired entries. Before retrying a queued request, the original caller's `CancellationToken` is checked to avoid wasting LLM tokens on abandoned work.

## Dependencies

| Section | What It Provides |
|---------|-----------------|
| section-02-domain-resilience | `ProviderExhaustedException` (thrown when all providers fail, caught to trigger queueing), `ProviderHealthState` enum |
| section-03-otel-conventions | `ResilienceConventions.QueueSize` and `ResilienceConventions.QueueExpired` metric name constants |
| section-04-config-and-validation | `DegradedModeConfig` with `RetryQueueTtlSeconds` (default 300) and `MaxQueueSize` (default 100); nested in `ResilienceConfig.DegradedMode` |
| section-07-resilience-interfaces | `IProviderHealthMonitor` interface -- `IsAnyProviderHealthy()` for drain checks, `OnCircuitStateChanged` event for drain triggers |
| section-13-health-monitor | `PollyProviderHealthMonitor` implementation that fires `OnCircuitStateChanged` when a provider transitions to `Healthy` |

## Blocked By This Section

| Section | Why |
|---------|-----|
| section-19-di-registration | Conditional `IHostedService` registration of `LlmRetryQueue` when `ResilienceConfig.Enabled == true` |

---

## Tests FIRST

**File:** `src/Content/Tests/Infrastructure.AI.Tests/Resilience/LlmRetryQueueTests.cs`

**Test framework:** xUnit + Moq + FluentAssertions. Naming: `MethodName_Scenario_ExpectedResult`. Arrange-Act-Assert.

```csharp
namespace Infrastructure.AI.Tests.Resilience;

using Microsoft.Extensions.AI;

/// <summary>
/// Tests for LlmRetryQueue -- the in-memory retry queue with TTL enforcement
/// and circuit-recovery-triggered drain. Tests run against the public methods
/// directly (EnqueueAsync, DrainAsync, SweepExpired) without starting the
/// BackgroundService lifecycle.
/// </summary>
public sealed class LlmRetryQueueTests : IDisposable
{
    // Shared test setup:
    //   - Mock<IProviderHealthMonitor> with configurable IsAnyProviderHealthy
    //   - Mock<IChatClient> as the "resilient client" passed for drain retries
    //   - IOptionsMonitor<ResilienceConfig> with DegradedMode = { MaxQueueSize = 5, RetryQueueTtlSeconds = 10 }
    //   - ILogger<LlmRetryQueue> (NullLogger or Mock)
    //   - TimeProvider (use FakeTimeProvider for TTL tests)

    // Test: EnqueueAsync_AddsToQueue_ReturnsTaskCompletionSource
    //   Arrange: Create queue with MaxQueueSize = 5
    //   Act: Call EnqueueAsync with test messages and ChatOptions
    //   Assert: Returns a Task<ChatResponse> that is not yet completed.
    //           QueueDepth (exposed for testing or via OTel metric) == 1.

    // Test: EnqueueAsync_ExceedsMaxSize_RejectsOldest
    //   Arrange: Create queue with MaxQueueSize = 3. Enqueue 3 items.
    //   Act: Enqueue a 4th item.
    //   Assert: Queue depth stays at 3 (oldest evicted).
    //           The evicted item's Task completes with ProviderExhaustedException.
    //           The 4th item's Task is not yet completed.

    // Test: DrainAsync_ProviderRecovered_RetriesQueuedRequests
    //   Arrange: Enqueue 2 items. Mock IChatClient to return success.
    //            Mock IProviderHealthMonitor.IsAnyProviderHealthy() returns true.
    //   Act: Call DrainAsync.
    //   Assert: Both items' Tasks complete successfully with the ChatResponse.
    //           Queue depth == 0.
    //           IChatClient.GetResponseAsync called twice.

    // Test: DrainAsync_CallerCancelled_SkipsRequest
    //   Arrange: Enqueue 2 items. Cancel the first item's CancellationToken before drain.
    //            Mock IChatClient to return success.
    //   Act: Call DrainAsync.
    //   Assert: First item's Task completes as cancelled (TaskCanceledException or OperationCanceledException).
    //           Second item's Task completes successfully.
    //           IChatClient.GetResponseAsync called only once (skipped the cancelled one).

    // Test: TtlExpiry_CompletesWithProviderExhaustedException
    //   Arrange: Enqueue 1 item. Advance FakeTimeProvider past RetryQueueTtlSeconds.
    //   Act: Call SweepExpired.
    //   Assert: Item's Task completes with ProviderExhaustedException.
    //           Queue depth == 0.
    //           ResilienceMetrics.QueueExpired incremented (verify via mock or counter read).

    // Test: DrainAsync_SuccessfulRetry_CompletesTcs
    //   Arrange: Enqueue 1 item. Mock IChatClient returns a valid ChatResponse.
    //   Act: Call DrainAsync.
    //   Assert: The returned Task<ChatResponse> is completed with the response from IChatClient.
    //           Queue depth == 0.

    // Test: DrainAsync_RetryFails_RequeuesOrExpires
    //   Arrange: Enqueue 1 item. Mock IChatClient throws ProviderExhaustedException on retry.
    //   Act: Call DrainAsync.
    //   Assert: Item is re-enqueued (queue depth == 1) if TTL not exceeded.
    //           OR item's Task completes with ProviderExhaustedException if TTL exceeded.

    // Test: DrainAsync_NoHealthyProvider_DoesNotAttemptRetry
    //   Arrange: Enqueue 1 item. Mock IsAnyProviderHealthy() returns false.
    //   Act: Call DrainAsync.
    //   Assert: IChatClient.GetResponseAsync never called.
    //           Queue depth still 1.

    // Test: EnqueueAsync_QueueSize_MetricUpdated
    //   Arrange: Create queue.
    //   Act: Enqueue 3 items, drain 1.
    //   Assert: ResilienceMetrics.QueueSize gauge reflects the correct depth at each step.
}
```

### Testing Strategy Notes

The `LlmRetryQueue` extends `BackgroundService`, but tests should exercise the public/internal methods (`EnqueueAsync`, `DrainAsync`, `SweepExpired`) directly without starting the hosted service lifecycle. This avoids flaky time-dependent tests. The `ExecuteAsync` loop ties these operations together in production.

Use `Microsoft.Extensions.Time.Testing.FakeTimeProvider` (from `Microsoft.Extensions.TimeProvider.Testing` NuGet package, or built-in in .NET 10) for TTL tests. The queue should accept `TimeProvider` in its constructor to make time testable.

The `IChatClient` used for retry is the `ResilientChatClient` resolved from DI -- but in tests, a simple mock `IChatClient` suffices since we're testing queue behavior, not resilience pipeline behavior.

---

## Implementation

### File 1: `QueuedLlmRequest` Record

**File:** `src/Content/Infrastructure/Infrastructure.AI/Resilience/LlmRetryQueue.cs` (defined as a nested type or in the same file)

`QueuedLlmRequest` is an internal record that bundles everything needed to retry a request:

```csharp
/// <summary>
/// Represents a queued LLM request awaiting retry after all providers were exhausted.
/// </summary>
internal sealed record QueuedLlmRequest
{
    /// <summary>The original chat messages to retry.</summary>
    required public IList<ChatMessage> Messages { get; init; }

    /// <summary>The original chat options (model, temperature, etc.).</summary>
    public ChatOptions? Options { get; init; }

    /// <summary>
    /// Completion source that callers await. Completed with the response on successful
    /// retry, or with <see cref="ProviderExhaustedException"/> on TTL expiry.
    /// </summary>
    required public TaskCompletionSource<ChatResponse> CompletionSource { get; init; }

    /// <summary>When this request was enqueued. Used for TTL enforcement.</summary>
    required public DateTimeOffset EnqueuedAt { get; init; }

    /// <summary>Absolute expiry time (EnqueuedAt + TTL).</summary>
    required public DateTimeOffset ExpiresAt { get; init; }

    /// <summary>
    /// The original caller's cancellation token. Checked before retry to avoid
    /// wasting LLM tokens on abandoned requests.
    /// </summary>
    required public CancellationToken CallerCancellation { get; init; }
}
```

### File 2: `LlmRetryQueue`

**File:** `src/Content/Infrastructure/Infrastructure.AI/Resilience/LlmRetryQueue.cs`

**Namespace:** `Infrastructure.AI.Resilience`

**Class:** `LlmRetryQueue : BackgroundService`

**Lifetime:** Singleton (registered as `IHostedService` conditionally in section-19)

#### Constructor Dependencies

```csharp
/// <summary>
/// In-memory retry queue for LLM requests that failed due to all providers being
/// exhausted. Monitors circuit breaker recovery via <see cref="IProviderHealthMonitor"/>
/// and automatically retries queued requests when a provider becomes healthy.
/// </summary>
/// <remarks>
/// <para>
/// Conditionally registered as <see cref="IHostedService"/> only when
/// <c>ResilienceConfig.Enabled == true</c>. When resilience is disabled, this
/// service is not in the DI container at all.
/// </para>
/// <para>
/// The queue is bounded by <see cref="DegradedModeConfig.MaxQueueSize"/>. When full,
/// the oldest request is evicted and its <see cref="TaskCompletionSource{T}"/> is
/// completed with <see cref="ProviderExhaustedException"/>.
/// </para>
/// <para>
/// TTL enforcement runs on a periodic sweep (every 10 seconds). Expired items have
/// their TCS completed with <see cref="ProviderExhaustedException"/>.
/// </para>
/// </remarks>
public sealed class LlmRetryQueue : BackgroundService
{
    // Constructor parameters:
    //   IProviderHealthMonitor healthMonitor,
    //   IResilientChatClientProvider chatClientProvider,
    //   IOptionsMonitor<ResilienceConfig> resilienceConfig,
    //   TimeProvider timeProvider,
    //   ILogger<LlmRetryQueue> logger
}
```

#### Internal State

- `ConcurrentQueue<QueuedLlmRequest> _queue` -- the bounded retry queue
- `int _queueDepth` -- tracked via `Interlocked` for O(1) depth queries (ConcurrentQueue.Count is O(n))
- `SemaphoreSlim _drainSignal` -- signaled by `OnCircuitStateChanged` callback to wake the drain loop
- `IChatClient? _cachedClient` -- lazily resolved from `IResilientChatClientProvider` on first drain

#### Public API

```csharp
/// <summary>
/// Enqueues a failed LLM request for automatic retry when a provider recovers.
/// Returns a Task that completes when the request is eventually retried successfully
/// or expires.
/// </summary>
/// <param name="messages">The original chat messages.</param>
/// <param name="options">The original chat options.</param>
/// <param name="callerCancellation">The original caller's cancellation token.</param>
/// <returns>A Task that completes with the ChatResponse on successful retry.</returns>
public Task<ChatResponse> EnqueueAsync(
    IList<ChatMessage> messages,
    ChatOptions? options,
    CancellationToken callerCancellation);
```

#### Core Behavior

**`EnqueueAsync`:**
1. Create a `TaskCompletionSource<ChatResponse>` with `TaskCreationOptions.RunContinuationsAsynchronously` (prevents blocking the enqueueing thread on TCS completion).
2. Calculate `expiresAt` from `TimeProvider.GetUtcNow()` + `DegradedModeConfig.RetryQueueTtlSeconds`.
3. Create `QueuedLlmRequest` record with all fields.
4. Enqueue to `_queue`, increment `_queueDepth` via `Interlocked.Increment`.
5. If `_queueDepth > MaxQueueSize`: dequeue the oldest, decrement depth, complete its TCS with `ProviderExhaustedException`, increment `ResilienceMetrics.QueueExpired`.
6. Update `ResilienceMetrics.QueueSize` gauge (set to current `_queueDepth`).
7. Return the TCS's `Task`.

**`ExecuteAsync` (BackgroundService override):**
1. Subscribe to `IProviderHealthMonitor.OnCircuitStateChanged` -- when any provider transitions to `Healthy`, release the `_drainSignal` semaphore.
2. Loop until `stoppingToken` is cancelled:
   a. `await _drainSignal.WaitAsync(TimeSpan.FromSeconds(10), stoppingToken)` -- wakes on signal OR every 10 seconds for TTL sweep.
   b. Call `SweepExpired()` to remove TTL-expired items.
   c. If `_healthMonitor.IsAnyProviderHealthy()`: call `DrainAsync(stoppingToken)`.
3. On shutdown: sweep all remaining items, complete their TCS with `OperationCanceledException`.

**`SweepExpired` (internal for testability):**
1. Snapshot the current time from `TimeProvider`.
2. Dequeue items one by one, checking `ExpiresAt`.
3. Expired items: complete TCS with `new ProviderExhaustedException(...)`, increment `ResilienceMetrics.QueueExpired`, decrement `_queueDepth`.
4. Non-expired items: re-enqueue (maintain ordering).
5. Update `ResilienceMetrics.QueueSize` gauge.

**`DrainAsync` (internal for testability):**
1. Lazily resolve `IChatClient` from `IResilientChatClientProvider.GetResilientChatClientAsync()` (cached after first resolution).
2. Dequeue items one by one:
   a. Check `CallerCancellation.IsCancellationRequested` -- if true, complete TCS with `TaskCanceledException`, decrement depth, skip to next.
   b. Check `ExpiresAt` -- if expired, handle as in `SweepExpired`.
   c. Try `await _cachedClient.GetResponseAsync(item.Messages, item.Options, callerCancellation)`.
   d. On success: complete TCS with the response, decrement depth, log at Debug level.
   e. On `ProviderExhaustedException`: re-enqueue the item (providers may have gone down again mid-drain), break the drain loop.
   f. On other exception: complete TCS with the exception, decrement depth, log at Warning level.
3. Update `ResilienceMetrics.QueueSize` gauge.

#### Thread Safety Considerations

- `ConcurrentQueue` handles concurrent enqueue/dequeue safely.
- `_queueDepth` tracked via `Interlocked.Increment`/`Decrement` -- never read `_queue.Count` (O(n) on ConcurrentQueue).
- `_drainSignal` is a `SemaphoreSlim(0, 1)` -- multiple rapid state changes coalesce into a single drain cycle. `Release()` is wrapped in a try/catch for `SemaphoreFullException` (already at max count).
- TCS uses `RunContinuationsAsynchronously` to prevent synchronous continuations from blocking queue operations.
- Only one drain/sweep runs at a time because `ExecuteAsync` is a single loop. No concurrent drain risk.

#### OTel Integration

The queue updates two metrics defined in `ResilienceMetrics` (section-03):

1. **`ResilienceMetrics.QueueSize`** (`UpDownCounter<long>`) -- updated on every enqueue, dequeue, eviction, and expiry. Reflects current queue depth.
2. **`ResilienceMetrics.QueueExpired`** (`Counter<long>`) -- incremented each time a request is evicted due to TTL or max-size overflow.

Metric calls are direct -- `ResilienceMetrics.QueueSize.Add(delta)` and `ResilienceMetrics.QueueExpired.Add(1)`. Tags are not needed (there's one global queue).

#### Wiring with IProviderHealthMonitor

In `ExecuteAsync`, the queue subscribes to `OnCircuitStateChanged`:

```csharp
// Pseudocode -- subscribe to health monitor in ExecuteAsync
_healthMonitor.OnCircuitStateChanged += (providerName, newState) =>
{
    if (newState == ProviderHealthState.Healthy)
    {
        try { _drainSignal.Release(); }
        catch (SemaphoreFullException) { /* coalesce -- already signaled */ }
    }
};
```

This subscription wakes the drain loop immediately when a provider recovers, rather than waiting for the 10-second sweep interval.

---

## Graceful Shutdown

When `stoppingToken` fires in `ExecuteAsync`:

1. Unsubscribe from `OnCircuitStateChanged`.
2. Drain remaining queue items by completing their TCS with `new OperationCanceledException("LlmRetryQueue shutting down")`.
3. Log the number of abandoned items at Warning level.

This prevents `TaskCompletionSource` instances from leaking (callers awaiting retry get a clean cancellation rather than hanging forever).

---

## Registration (section-19 will handle)

```csharp
// In Infrastructure.AI/DependencyInjection.cs -- conditional registration
if (resilienceConfig.Enabled)
{
    services.AddSingleton<LlmRetryQueue>();
    services.AddHostedService(sp => sp.GetRequiredService<LlmRetryQueue>());
}
```

The double registration pattern (singleton + hosted service forwarding) allows other services to inject `ILlmRetryQueue` for `EnqueueAsync` calls while also ensuring the background service lifecycle is managed by the host.

**Implementation note:** `ILlmRetryQueue` interface was extracted to `Application.AI.Common/Interfaces/Resilience/` per code review (MEDIUM-4). Callers depend on the interface, not the concrete `LlmRetryQueue` class.

---

## Implementation Deviations from Plan

1. **ILlmRetryQueue interface extracted** — Plan suggested concrete type; review identified clean architecture violation. Interface created in Application.AI.Common with `EnqueueAsync` as the single method.
2. **Lock added around enqueue+eviction** — Plan used lockless ConcurrentQueue; review identified eviction race under concurrent access (HIGH-1). Lightweight `lock (_enqueueLock)` added to guarantee oldest-first eviction.
3. **Caller cancellation during retry** — Plan's catch blocks didn't distinguish caller vs host cancellation. Added `OperationCanceledException` catch clauses to produce cancelled TCS (not faulted) when caller token fires mid-retry (MEDIUM-7).
4. **Dispose ordering fixed** — `base.Dispose()` now called before `_drainSignal.Dispose()` to avoid ObjectDisposedException race during shutdown (MEDIUM-6).
5. **InternalsVisibleTo added** — Infrastructure.AI.csproj exposes internals to Infrastructure.AI.Tests for `DrainAsync`, `SweepExpired`, and `QueueDepth` testing.

---

## File Checklist (Actual)

| File | Action | Project |
|------|--------|---------|
| `src/Content/Application/Application.AI.Common/Interfaces/Resilience/ILlmRetryQueue.cs` | Create | Application.AI.Common |
| `src/Content/Infrastructure/Infrastructure.AI/Resilience/LlmRetryQueue.cs` | Create | Infrastructure.AI |
| `src/Content/Tests/Infrastructure.AI.Tests/Resilience/LlmRetryQueueTests.cs` | Create | Infrastructure.AI.Tests |
| `src/Content/Infrastructure/Infrastructure.AI/Infrastructure.AI.csproj` | Modified | InternalsVisibleTo added |
| `src/Content/Tests/Infrastructure.AI.Tests/Infrastructure.AI.Tests.csproj` | Modified | TimeProvider.Testing package added |
| `src/Directory.Packages.props` | Modified | TimeProvider.Testing version added |

## Tests (8 passing)

1. `EnqueueAsync_AddsToQueue_ReturnsIncompleteTask`
2. `EnqueueAsync_ExceedsMaxSize_RejectsOldest`
3. `DrainAsync_ProviderRecovered_RetriesQueuedRequests`
4. `DrainAsync_CallerCancelled_SkipsRequest`
5. `SweepExpired_TtlExpired_CompletesWithProviderExhaustedException`
6. `DrainAsync_SuccessfulRetry_CompletesTcs`
7. `DrainAsync_RetryFails_RequeuesItem`
8. `DrainAsync_NoHealthyProvider_DoesNotAttemptRetry`

---

## Verification

```
dotnet build src/Content/Infrastructure/Infrastructure.AI/Infrastructure.AI.csproj
dotnet test src/Content/Tests/Infrastructure.AI.Tests/Infrastructure.AI.Tests.csproj --filter "FullyQualifiedName~LlmRetryQueueTests"
```
