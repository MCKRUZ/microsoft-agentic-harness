# Section 13: Health Monitor -- PollyProviderHealthMonitor

## Overview

This section implements `PollyProviderHealthMonitor`, which bridges Polly v8 circuit breaker state to the domain `ProviderHealthState` enum. It exposes per-provider health through the `IProviderHealthMonitor` interface (defined in section-07) and fires state-change callbacks for OTel gauge updates and retry queue drain triggers.

The health monitor does **not** perform synthetic pre-warm probes. LLM API calls cost tokens and providers have no lightweight health endpoint. Instead, Polly's default half-open behavior lets the next real request serve as the recovery probe. The monitor tracks and exposes these transitions.

## Dependencies

| Section | What It Provides |
|---------|-----------------|
| section-02-domain-resilience | `ProviderHealthState` enum (`Healthy`, `Degraded`, `Unavailable`) |
| section-03-otel-conventions | `ResilienceConventions` constants for circuit state metric names |
| section-07-resilience-interfaces | `IProviderHealthMonitor` interface contract |
| section-11-polly-pipelines | `ProviderResiliencePipelineBuilder` which creates per-provider `CircuitBreakerStateProvider` instances |

## Blocked By This Section

| Section | Why |
|---------|-----|
| section-15-retry-queue | `LlmRetryQueue` monitors `OnCircuitStateChanged` to trigger drain when a provider recovers |
| section-16-resilient-provider | `ResilientChatClientProvider` injects `IProviderHealthMonitor` for pre-flight health checks |

---

## Tests FIRST

**File:** `src/Content/Tests/Infrastructure.AI.Tests/Resilience/PollyProviderHealthMonitorTests.cs`

**Test framework:** xUnit + Moq + FluentAssertions. Naming: `MethodName_Scenario_ExpectedResult`. Arrange-Act-Assert.

```csharp
// PollyProviderHealthMonitorTests.cs

// Test: GetProviderHealth_CircuitClosed_ReturnsHealthy
//   Arrange: Register a provider whose CircuitBreakerStateProvider reports CircuitState.Closed
//   Act:     Call GetProviderHealth("provider-a")
//   Assert:  Returns ProviderHealthState.Healthy

// Test: GetProviderHealth_CircuitHalfOpen_ReturnsDegraded
//   Arrange: Register a provider whose CircuitBreakerStateProvider reports CircuitState.HalfOpen
//   Act:     Call GetProviderHealth("provider-a")
//   Assert:  Returns ProviderHealthState.Degraded

// Test: GetProviderHealth_CircuitOpen_ReturnsUnavailable
//   Arrange: Register a provider whose CircuitBreakerStateProvider reports CircuitState.Open
//   Act:     Call GetProviderHealth("provider-a")
//   Assert:  Returns ProviderHealthState.Unavailable

// Test: GetProviderHealth_CircuitIsolated_ReturnsUnavailable
//   Arrange: Register a provider whose CircuitBreakerStateProvider reports CircuitState.Isolated
//   Act:     Call GetProviderHealth("provider-a")
//   Assert:  Returns ProviderHealthState.Unavailable

// Test: GetProviderHealth_UnknownProvider_ReturnsHealthy
//   Arrange: No providers registered
//   Act:     Call GetProviderHealth("unknown-provider")
//   Assert:  Returns ProviderHealthState.Healthy (default assumption: unknown == healthy)

// Test: GetAllProviderHealth_ReturnsAllProviders
//   Arrange: Register 3 providers with Closed, HalfOpen, Open states
//   Act:     Call GetAllProviderHealth()
//   Assert:  Dictionary has 3 entries with correct mapped states

// Test: IsAnyProviderHealthy_AllOpen_ReturnsFalse
//   Arrange: Register 2 providers both with CircuitState.Open
//   Act:     Call IsAnyProviderHealthy()
//   Assert:  Returns false

// Test: IsAnyProviderHealthy_OneClosed_ReturnsTrue
//   Arrange: Register 2 providers, one Open, one Closed
//   Act:     Call IsAnyProviderHealthy()
//   Assert:  Returns true

// Test: IsAnyProviderHealthy_NoProviders_ReturnsTrue
//   Arrange: No providers registered
//   Act:     Call IsAnyProviderHealthy()
//   Assert:  Returns true (vacuously -- no unhealthy providers)

// Test: OnCircuitStateChanged_Fires_OnTransition
//   Arrange: Register a provider, subscribe to OnCircuitStateChanged callback
//   Act:     Simulate a state transition (Closed -> Open)
//   Assert:  Callback fires with provider name, old state, new state
```

### Testing Strategy Notes

Polly's `CircuitBreakerStateProvider` is a concrete class whose `CircuitState` property is read-only and controlled internally by the pipeline. For unit testing, there are two approaches:

1. **Wrapper/abstraction approach**: Define a thin `ICircuitStateAccessor` interface that wraps `CircuitBreakerStateProvider.CircuitState`. The production implementation reads from the real Polly provider. Tests can mock the interface.

2. **Integration-style approach**: Build a real Polly pipeline with aggressive thresholds (1 failure opens circuit), trigger failures to move through states, and verify the monitor maps correctly.

Recommend **approach 1** for unit tests (fast, deterministic) and optionally a single integration test using approach 2 to verify the Polly wiring is correct.

The `OnCircuitStateChanged` event detection requires periodic polling of each provider's circuit state (since Polly v8 `CircuitBreakerStateProvider` does not expose a state-change event natively). The monitor should poll on each `GetProviderHealth` call and compare against cached state, or use Polly's `OnOpened`/`OnClosed`/`OnHalfOpen` callbacks registered during pipeline construction (section-11).

The recommended approach is the **callback approach**: `ProviderResiliencePipelineBuilder` (section-11) registers `OnOpened`, `OnClosed`, and `OnHalfOpen` callbacks that call into `PollyProviderHealthMonitor.ReportStateChange(providerName, newState)`. This avoids polling, is immediate, and simplifies testing -- tests call `ReportStateChange` directly.

---

## Implementation

### File: `src/Content/Infrastructure/Infrastructure.AI/Resilience/PollyProviderHealthMonitor.cs`

**Namespace:** `Infrastructure.AI.Resilience`

**Class:** `PollyProviderHealthMonitor : IProviderHealthMonitor`

**Lifetime:** Singleton (registered in section-19)

#### Constructor Dependencies

- `ILogger<PollyProviderHealthMonitor>` -- log state transitions at Information level

#### Internal State

- `ConcurrentDictionary<string, ProviderHealthState> _providerStates` -- tracks last known state per provider
- `Action<string, ProviderHealthState>? OnCircuitStateChanged` -- event for external subscribers (retry queue, OTel gauges)

#### Public API (implements `IProviderHealthMonitor`)

```csharp
/// <summary>Returns the current health state of the named provider.</summary>
ProviderHealthState GetProviderHealth(string providerName);

/// <summary>Returns health state for all registered providers.</summary>
IReadOnlyDictionary<string, ProviderHealthState> GetAllProviderHealth();

/// <summary>True if at least one provider is Healthy, or no providers are registered.</summary>
bool IsAnyProviderHealthy();
```

#### Internal API (called by Polly callbacks in section-11)

```csharp
/// <summary>
/// Called by Polly pipeline callbacks (OnOpened, OnClosed, OnHalfOpen) to report
/// circuit breaker state transitions. Maps Polly states to domain ProviderHealthState
/// and fires OnCircuitStateChanged if the state actually changed.
/// </summary>
void ReportStateChange(string providerName, ProviderHealthState newState);
```

#### State Mapping Logic

| Polly `CircuitState` | Domain `ProviderHealthState` |
|----------------------|------------------------------|
| `Closed`             | `Healthy`                    |
| `HalfOpen`           | `Degraded`                   |
| `Open`               | `Unavailable`                |
| `Isolated`           | `Unavailable`                |

Note: The mapping itself lives in this class. The Polly callbacks in section-11 translate the Polly callback type (`OnOpened` -> `Unavailable`, `OnClosed` -> `Healthy`, `OnHalfOpen` -> `Degraded`) before calling `ReportStateChange`.

#### Behavior Details

1. **`GetProviderHealth(providerName)`**: Looks up the `_providerStates` dictionary. Returns `ProviderHealthState.Healthy` for unknown providers (default assumption -- if we don't track it, it hasn't failed).

2. **`GetAllProviderHealth()`**: Returns a snapshot (new `ReadOnlyDictionary` wrapping a copy) to prevent external mutation.

3. **`IsAnyProviderHealthy()`**: Returns `true` if the dictionary is empty (no providers registered means nothing is known to be unhealthy) OR if any entry has `ProviderHealthState.Healthy`.

4. **`ReportStateChange(providerName, newState)`**: 
   - Read current state from dictionary (default `Healthy` if absent)
   - If unchanged, return early (no-op)
   - Update dictionary via `AddOrUpdate`
   - Log at `Information` level: `"Provider {ProviderName} circuit state changed: {OldState} -> {NewState}"`
   - Invoke `OnCircuitStateChanged?.Invoke(providerName, newState)`
   - Record OTel metric: increment `ResilienceMetrics.CircuitStateChanges` with tags `provider_name` and `transition` (e.g., `healthy_to_unavailable`)
   - Update `ResilienceMetrics.CircuitStateGauge` for the provider (0=Healthy, 1=Degraded, 2=Unavailable)

5. **Thread safety**: `ConcurrentDictionary` handles concurrent reads/writes. The `ReportStateChange` method may have a brief race between read-current and update, but the worst case is a duplicate callback invocation -- acceptable for metrics/events, not a correctness issue.

#### Wiring with Polly Callbacks (section-11 integration point)

The `ProviderResiliencePipelineBuilder` (section-11) must be updated to accept `PollyProviderHealthMonitor` and register callbacks during pipeline construction:

```csharp
// In ProviderResiliencePipelineBuilder.Build() -- pseudocode showing the callback wiring
.AddCircuitBreaker(new CircuitBreakerStrategyOptions<ChatResponse>
{
    // ... existing config ...
    OnOpened = args =>
    {
        healthMonitor.ReportStateChange(providerName, ProviderHealthState.Unavailable);
        return default;
    },
    OnClosed = args =>
    {
        healthMonitor.ReportStateChange(providerName, ProviderHealthState.Healthy);
        return default;
    },
    OnHalfOpened = args =>
    {
        healthMonitor.ReportStateChange(providerName, ProviderHealthState.Degraded);
        return default;
    }
})
```

This means `ProviderResiliencePipelineBuilder.Build()` signature (defined in section-11) should accept the health monitor as a parameter. If section-11 is already implemented without this parameter, it needs a minor update to thread the monitor through.

---

## OTel Integration

The health monitor records two metrics on state change (using conventions from section-03):

1. **`ResilienceConventions.CircuitStateChanges`** -- counter, incremented per transition, tagged with `provider_name` and `transition` (e.g., `"healthy_to_degraded"`)
2. **`ResilienceConventions.CircuitState`** -- gauge per provider, set to 0/1/2 corresponding to Healthy/Degraded/Unavailable

These instruments are defined in `ResilienceMetrics` (section-03). The health monitor calls them directly -- no indirection needed.

---

## Registration (section-19 will handle)

```csharp
// In Infrastructure.AI/DependencyInjection.cs
services.AddSingleton<PollyProviderHealthMonitor>();
services.AddSingleton<IProviderHealthMonitor>(sp => sp.GetRequiredService<PollyProviderHealthMonitor>());
```

The double registration ensures both `IProviderHealthMonitor` (for consumers like section-15, section-16) and `PollyProviderHealthMonitor` (for `ProviderResiliencePipelineBuilder` which needs the concrete type to call `ReportStateChange`) resolve to the same singleton instance.

---

## File Checklist

| File | Action | Project |
|------|--------|---------|
| `src/Content/Infrastructure/Infrastructure.AI/Resilience/PollyProviderHealthMonitor.cs` | Create | Infrastructure.AI |
| `src/Content/Tests/Infrastructure.AI.Tests/Resilience/PollyProviderHealthMonitorTests.cs` | Create | Infrastructure.AI.Tests |

---

## Verification

```
dotnet build src/Content/Infrastructure/Infrastructure.AI/Infrastructure.AI.csproj
dotnet test src/Content/Tests/Infrastructure.AI.Tests/Infrastructure.AI.Tests.csproj --filter "FullyQualifiedName~PollyProviderHealthMonitorTests"
```

---

## Implementation Notes

**Deviations from plan:**
- `ReportStateChange` uses `AddOrUpdate` instead of `GetOrAdd` + indexer assignment — eliminates TOCTOU race condition for concurrent Polly callbacks on the same provider.
- Event invocation captures delegate to local variable before `?.Invoke` — standard C# thread-safe event pattern.
- No `ICircuitStateAccessor` wrapper needed — tests call `ReportStateChange` directly, which is the production code path from Polly callbacks.
- Constructor takes nullable `ILogger<PollyProviderHealthMonitor>?` for test simplicity (`new(null)`).

**Final test count:** 11 (all passing, ~77ms total duration)
- State mapping: explicit healthy, degraded, unavailable, unknown provider
- Aggregate queries: GetAllProviderHealth (3 providers), IsAnyProviderHealthy (all unavailable, one healthy, no providers)
- Events: fires on transition, does not fire when unchanged, concurrent calls fire exactly once
