# Section 11: Polly Per-Provider Resilience Pipelines

## Overview

This section implements `ProviderResiliencePipelineBuilder`, which constructs a `ResiliencePipeline<ChatResponse>` for each provider in the fallback chain. Each provider gets its own independent resilience pipeline with retry, circuit breaker, and timeout strategies. One provider's failures never affect another's circuit state.

The builder lives in `Infrastructure.AI/Resilience/` and is consumed by `ResilientChatClient` (section 12) to wrap each provider's `IChatClient` calls.

**Important design note:** The cross-provider fallback is NOT a Polly fallback strategy. It is a simple iteration loop in `ResilientChatClient`. Polly handles per-provider retry, circuit breaking, and timeouts only. This separation keeps each provider's failure domain isolated and allows metadata collection across the chain.

## Dependencies

These must be implemented before this section:

| Section | What It Provides |
|---------|-----------------|
| **section-02-domain-resilience** | `ProviderHealthState` enum, `FallbackMetadata` record, `ProviderExhaustedException` in `Domain.AI/Resilience/` |
| **section-03-otel-conventions** | `ResilienceConventions` static class in `Domain.AI/Telemetry/Conventions/` with metric name constants |
| **section-04-config-and-validation** | `ResilienceConfig`, `CircuitBreakerConfig`, `RetryConfig`, `TimeoutConfig` in `Domain.Common/Config/AI/Resilience/` |
| **section-07-resilience-interfaces** | `IProviderHealthMonitor` in `Application.AI.Common/Interfaces/Resilience/` |

## NuGet Package Addition

`Polly.Core` must be added to the central package management and referenced from `Infrastructure.AI`.

### File: `src/Directory.Packages.props`

Add inside the `<ItemGroup>` under the `<!-- Infrastructure.AI -->` comment block:

```xml
<PackageVersion Include="Polly.Core" Version="8.5.2" />
```

The exact version should be the latest stable Polly.Core 8.x at implementation time. The codebase already uses `Microsoft.Extensions.Http.Resilience` (which depends on Polly.Core internally) in `Infrastructure.APIAccess`, but `Infrastructure.AI` needs a direct reference since it builds pipelines programmatically rather than through the HTTP resilience extensions.

### File: `src/Content/Infrastructure/Infrastructure.AI/Infrastructure.AI.csproj`

Add to the `<ItemGroup>` with PackageReferences:

```xml
<PackageReference Include="Polly.Core" />
```

## Tests First

### File: `src/Content/Tests/Infrastructure.AI.Tests/Resilience/ProviderResiliencePipelineTests.cs`

Test class: `ProviderResiliencePipelineTests`

All tests use xUnit + Moq + FluentAssertions. Naming: `MethodName_Scenario_ExpectedResult`. Arrange-Act-Assert pattern.

The tests exercise pipelines built by `ProviderResiliencePipelineBuilder` by executing delegates through them and verifying retry/circuit/timeout behavior.

```csharp
/// <summary>
/// Tests for ProviderResiliencePipelineBuilder -- verifies that per-provider
/// resilience pipelines correctly compose retry, circuit breaker, and timeout
/// strategies with config-driven parameters.
/// </summary>
public class ProviderResiliencePipelineTests
{
    // --- Retry behavior ---

    // Test: Pipeline_TransientError_RetriesToConfiguredMax
    // Arrange: Build pipeline with MaxAttempts=2. Create a delegate that fails twice
    //          with HttpRequestException then succeeds.
    // Act:    Execute through pipeline.
    // Assert: Delegate called 3 times (1 initial + 2 retries). Returns success.

    // Test: Pipeline_Http429_TriggersRetry
    // Arrange: Build pipeline with default config. Create delegate that throws
    //          HttpRequestException with StatusCode 429 on first call, succeeds on second.
    // Act:    Execute through pipeline.
    // Assert: Returns success on second attempt. Delegate called twice.

    // Test: Pipeline_Http500_TriggersRetry
    // Arrange: Same as above but with StatusCode 500.
    // Act:    Execute through pipeline.
    // Assert: Returns success on second attempt.

    // --- Circuit breaker behavior ---

    // Test: Pipeline_FailureRatioExceeded_OpensCircuit
    // Arrange: Build pipeline with FailureRatio=0.5, MinimumThroughput=2,
    //          SamplingDuration=30s. Execute enough failures to trip the circuit.
    // Act:    Execute one more call after circuit should be open.
    // Assert: Throws BrokenCircuitException without invoking the delegate.

    // Test: Pipeline_CircuitOpen_ThrowsBrokenCircuitException
    // Arrange: Build pipeline, trip the circuit (as above).
    // Act:    Execute through pipeline.
    // Assert: BrokenCircuitException thrown. Inner delegate NOT called.

    // Test: Pipeline_SuccessAfterRetry_ResetsCircuit
    // Arrange: Build pipeline, cause some failures (but not enough to open circuit),
    //          then succeed.
    // Act:    Execute a subsequent call.
    // Assert: Call succeeds -- circuit remains closed.

    // --- Timeout behavior ---

    // Test: Pipeline_Timeout_CancelsAttempt
    // Arrange: Build pipeline with PerAttemptSeconds=1. Create delegate that
    //          delays for 5 seconds.
    // Act:    Execute through pipeline.
    // Assert: Throws TimeoutRejectedException (Polly's timeout exception).
    //         Delegate's CancellationToken was signaled.

    // --- Config application ---

    // Test: Pipeline_ConfigValues_AppliedCorrectly
    // Arrange: Build pipeline with explicit non-default config values
    //          (MaxAttempts=5, FailureRatio=0.8, PerAttemptSeconds=60).
    // Act:    Exercise retry behavior with a failing delegate.
    // Assert: Pipeline retries 5 times (not the default 2).
    //         This validates that config flows through to the Polly strategies.
}
```

**Testing approach notes:**

- Do NOT mock Polly. Build real `ResiliencePipeline<ChatResponse>` instances using the builder and execute real delegates. Polly v8 pipelines are designed to be testable this way.
- For circuit breaker tests, use `SamplingDuration` of a few seconds and `MinimumThroughput` of 2 to make the circuit trip quickly in tests.
- For timeout tests, use `PerAttemptSeconds=1` and a delegate that `Task.Delay`s for longer.
- The delegate signature is `Func<ResilienceContext, CancellationToken, ValueTask<ChatResponse>>` (Polly v8 typed pipeline).
- Create a helper method in the test class to build a `ChatResponse` stub for successful results.
- Create a helper to build `ResilienceConfig` with test-friendly defaults (short durations, low thresholds).

## Implementation

### File: `src/Content/Infrastructure/Infrastructure.AI/Resilience/ProviderResiliencePipelineBuilder.cs`

This is a static builder class (or a non-static class if you need constructor-injected dependencies like `ILogger` -- prefer static with a logger parameter for simplicity).

**Responsibilities:**
- Accept a provider name and `ResilienceConfig` (Options pattern)
- Build and return a `ResiliencePipeline<ChatResponse>` with three strategies layered in the correct order
- Expose the `CircuitBreakerStateProvider` for each built pipeline so `PollyProviderHealthMonitor` (section 13) can query circuit state

**Strategy composition order** (outermost to innermost):

1. **Retry** (outermost within provider) -- wraps circuit breaker and timeout
2. **Circuit Breaker** -- wraps timeout
3. **Timeout** (innermost) -- per-attempt deadline

This ordering means: a single attempt has a timeout. If it fails, the circuit breaker records the failure. If retries remain, the retry strategy tries again (going back through circuit breaker and timeout).

**Key design decisions:**

- **`ShouldHandle` predicate for retry**: Handle `HttpRequestException` (covers 429, 500, 503), `TaskCanceledException` (timeout-induced), and `TimeoutRejectedException` (Polly's own timeout). Do NOT retry `BrokenCircuitException` -- that means the circuit is open and retrying is pointless.
- **Circuit breaker is ratio-based** (`CircuitBreakerStrategyOptions`), not consecutive-failure-based. This is more resilient to intermittent failures.
- **No rate limiter** -- LLM providers have their own rate limiting; adding client-side rate limiting would double-count and reduce throughput unnecessarily. The retry with backoff handles 429 responses.
- **Jitter on retry delays** -- always enable `UseJitter = true` to prevent thundering herd on provider recovery.

**Builder method signature:**

```csharp
/// <summary>
/// Builds a per-provider resilience pipeline for LLM chat completion calls.
/// Each provider gets independent retry, circuit breaker, and timeout strategies.
/// </summary>
public static class ProviderResiliencePipelineBuilder
{
    /// <summary>
    /// Creates a <see cref="ResiliencePipeline{ChatResponse}"/> for the named provider
    /// using the supplied resilience configuration.
    /// </summary>
    /// <param name="providerName">
    /// Logical name for this provider (used in OTel tags and circuit breaker isolation).
    /// </param>
    /// <param name="config">Resilience configuration from Options pattern.</param>
    /// <param name="circuitBreakerStateProvider">
    /// Output: the Polly <see cref="CircuitBreakerStateProvider"/> for this pipeline,
    /// used by <see cref="PollyProviderHealthMonitor"/> to query circuit state.
    /// </param>
    /// <param name="logger">Logger for retry/circuit events.</param>
    /// <returns>A configured resilience pipeline scoped to this provider.</returns>
    public static ResiliencePipeline<ChatResponse> Build(
        string providerName,
        ResilienceConfig config,
        out CircuitBreakerStateProvider circuitBreakerStateProvider,
        ILogger? logger = null)
    {
        // Implementation builds pipeline with:
        // 1. RetryStrategyOptions<ChatResponse>
        // 2. CircuitBreakerStrategyOptions<ChatResponse>
        // 3. TimeoutStrategyOptions
    }
}
```

**Configuration mapping** (from `ResilienceConfig` sub-objects):

| Config Property | Polly Strategy | Polly Option |
|----------------|---------------|-------------|
| `RetryConfig.MaxAttempts` | Retry | `MaxRetryAttempts` |
| `RetryConfig.BaseDelaySeconds` | Retry | `Delay` (as `TimeSpan.FromSeconds`) |
| `RetryConfig.BackoffType` | Retry | `BackoffType` (map string to `DelayBackoffType`) |
| `CircuitBreakerConfig.FailureRatio` | Circuit Breaker | `FailureRatio` |
| `CircuitBreakerConfig.SamplingDurationSeconds` | Circuit Breaker | `SamplingDuration` |
| `CircuitBreakerConfig.MinimumThroughput` | Circuit Breaker | `MinimumThroughput` |
| `CircuitBreakerConfig.BreakDurationSeconds` | Circuit Breaker | `BreakDuration` |
| `TimeoutConfig.PerAttemptSeconds` | Timeout | `Timeout` |

**Default config values** (used when config sections are missing or properties unset):

| Property | Default |
|----------|---------|
| `MaxAttempts` | 2 |
| `BaseDelaySeconds` | 2 |
| `BackoffType` | `Exponential` |
| `FailureRatio` | 0.5 |
| `SamplingDurationSeconds` | 30 |
| `MinimumThroughput` | 5 |
| `BreakDurationSeconds` | 60 |
| `PerAttemptSeconds` | 30 |

**CircuitBreakerStateProvider extraction:**

Polly v8's `CircuitBreakerStrategyOptions` exposes a `StateProvider` property. Configure a `CircuitBreakerStateProvider` and set it:

```csharp
var stateProvider = new CircuitBreakerStateProvider();
new CircuitBreakerStrategyOptions<ChatResponse>
{
    StateProvider = stateProvider,
    // ... other options
};
```

The `stateProvider` is then returned via the `out` parameter so `PollyProviderHealthMonitor` can call `stateProvider.CircuitState` to read `Closed`, `HalfOpen`, `Open`, or `Isolated`.

**OTel integration within the pipeline:**

The builder should attach `OnRetry` and `OnOpened`/`OnClosed`/`OnHalfOpened` callbacks that:
- Log at appropriate levels (retry at `Warning`, circuit open at `Error`, circuit close at `Information`)
- Increment `ResilienceMetrics` counters with provider name tags using `ResilienceConventions` constants
- Record the provider name as a tag dimension on all metric emissions

Example callback wiring (conceptual):

```csharp
// In RetryStrategyOptions<ChatResponse>:
OnRetry = args =>
{
    ResilienceMetrics.RetryAttempts.Add(1,
        new(ResilienceConventions.ProviderName, providerName),
        new(ResilienceConventions.AttemptNumber, args.AttemptNumber));
    logger?.LogWarning("Retry {Attempt} for provider {Provider}: {Exception}",
        args.AttemptNumber, providerName, args.Outcome.Exception?.Message);
    return default;
}

// In CircuitBreakerStrategyOptions<ChatResponse>:
OnOpened = args =>
{
    ResilienceMetrics.CircuitStateChanges.Add(1,
        new(ResilienceConventions.ProviderName, providerName),
        new(ResilienceConventions.CircuitTransition, "opened"));
    logger?.LogError("Circuit opened for provider {Provider}", providerName);
    return default;
}
```

**Streaming resilience note:**

`ResiliencePipeline<ChatResponse>` works for `GetResponseAsync` (returns `ChatResponse`). For `GetStreamingResponseAsync` (returns `IAsyncEnumerable<ChatResponseUpdate>`), a separate non-generic `ResiliencePipeline` wraps the stream initiation only. The `ProviderResiliencePipelineBuilder` should expose a second build method or the `ResilientChatClient` (section 12) should build the non-generic pipeline from the same config. Recommended: add a second method:

```csharp
/// <summary>
/// Builds a non-generic resilience pipeline for wrapping stream initiation.
/// Uses the same retry/circuit/timeout config but operates on the void-returning
/// initiation call rather than the stream content.
/// </summary>
public static ResiliencePipeline BuildForStreamInitiation(
    string providerName,
    ResilienceConfig config,
    CircuitBreakerStateProvider sharedStateProvider,
    ILogger? logger = null)
```

This second pipeline shares the same `CircuitBreakerStateProvider` as the typed pipeline so circuit state is consistent across sync and streaming calls for the same provider. The circuit breaker state should be the same object -- a circuit that opens due to streaming failures should also block non-streaming calls and vice versa.

## Existing Pattern Reference

The codebase has an existing Polly pipeline in `Infrastructure.APIAccess/Common/Extensions/IServiceCollectionExtensions.cs` (line 274-317) that uses `AddRetry`, `AddTimeout`, `AddCircuitBreaker`, and `AddRateLimiter` on an `IHttpClientFactory` resilience pipeline. This section follows the same strategy composition pattern but builds the pipeline programmatically (not through `AddResiliencePipeline` DI extension) because:

1. The pipeline wraps `IChatClient` calls, not `HttpClient` calls
2. Each provider needs its own independent pipeline instance
3. We need the `CircuitBreakerStateProvider` reference for health monitoring

## File Checklist

| File | Action | Project |
|------|--------|---------|
| `src/Directory.Packages.props` | Modify -- add `Polly.Core` package version | Solution |
| `src/Content/Infrastructure/Infrastructure.AI/Infrastructure.AI.csproj` | Modify -- add `Polly.Core` PackageReference | Infrastructure.AI |
| `src/Content/Infrastructure/Infrastructure.AI/Resilience/ProviderResiliencePipelineBuilder.cs` | Create | Infrastructure.AI |
| `src/Content/Tests/Infrastructure.AI.Tests/Resilience/ProviderResiliencePipelineTests.cs` | Create | Infrastructure.AI.Tests |

## Downstream Consumers

- **section-12-resilient-chat-client**: `ResilientChatClient` calls `ProviderResiliencePipelineBuilder.Build()` for each provider in the chain, then executes `IChatClient` calls through the returned pipelines.
- **section-13-health-monitor**: `PollyProviderHealthMonitor` receives the `CircuitBreakerStateProvider` instances (one per provider) from the build step and maps their `CircuitState` to `ProviderHealthState`.
- **section-16-resilient-provider**: `ResilientChatClientProvider` orchestrates building the full chain, calling the builder for each `FallbackProviderConfig` entry.

---

## Verification

```
dotnet build src/Content/Infrastructure/Infrastructure.AI/Infrastructure.AI.csproj
dotnet test src/Content/Tests/Infrastructure.AI.Tests/Infrastructure.AI.Tests.csproj --filter "FullyQualifiedName~ProviderResiliencePipelineTests"
```

---

## Implementation Notes

**Deviations from plan:**
- `RetryConfig.MaxAttempts` is converted to Polly's `MaxRetryAttempts` via `Math.Max(1, MaxAttempts - 1)` since the config semantics are "total attempts including initial" but Polly expects "retries after initial". This was caught in code review.
- `ParseBackoffType` now throws `ArgumentException` on unknown values instead of silently defaulting to Exponential.
- `BuildForStreamInitiation` documents that circuits are independent per-pipeline — `CircuitBreakerStateProvider` is for read-only health queries only, not cross-pipeline synchronization.
- Added `TransitionFrom` tag to `CircuitStateChanges` metric recordings.
- Removed unused `using System.Diagnostics`.

**Final test count:** 8 (all passing, ~2s total duration including timeout test)
