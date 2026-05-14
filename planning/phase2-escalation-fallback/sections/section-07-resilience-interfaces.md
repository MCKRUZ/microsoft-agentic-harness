# Section 07: Resilience Interfaces

## Overview

This section defines the two application-layer interfaces that the entire fallback chain subsystem depends on: `IResilientChatClientProvider` and `IProviderHealthMonitor`. These are placed in `Application.AI.Common/Interfaces/Resilience/` following the same interface organization pattern used by `Interfaces/Agents/`, `Interfaces/Governance/`, and `Interfaces/MetaHarness/`.

Both interfaces depend solely on domain types from `Domain.AI/Resilience/` (section-02). They are consumed by Infrastructure.AI implementations in later sections (sections 11-16) and by the `AgentExecutionContextFactory` integration (section-16).

## Dependencies

- **section-02-domain-resilience**: Provides `ProviderHealthState` enum and `FallbackMetadata` record used in the interface signatures.

## Downstream Consumers

These interfaces block the following sections:
- **section-11-polly-pipelines** -- `ProviderResiliencePipelineBuilder` builds pipelines consumed by `IResilientChatClientProvider` implementation
- **section-12-resilient-chat-client** -- `ResilientChatClient` is the `IChatClient` returned by `IResilientChatClientProvider`
- **section-13-health-monitor** -- `PollyProviderHealthMonitor` implements `IProviderHealthMonitor`
- **section-16-resilient-provider** -- `ResilientChatClientProvider` implements `IResilientChatClientProvider`

---

## Tests

Tests for interfaces are minimal since these are pure contract definitions with no behavior. The TDD plan specifies integration-level tests that validate the implementations resolve correctly through DI and that the `AgentExecutionContextFactory` selects the right provider. Those tests belong to sections 16, 19, and 21. However, this section should confirm the interface types compile and are accessible from the expected namespaces.

The following test stubs from the TDD plan are relevant to these interfaces (they will be fully implemented in section-21 after implementations exist):

```csharp
// File: src/Content/Tests/Infrastructure.AI.Tests/Resilience/ResilientChatClientTests.cs
// These tests validate the contract defined by IResilientChatClientProvider:

// Test: GetResponseAsync_PrimarySucceeds_NoFallback_MetadataShowsPrimary
// Test: GetResponseAsync_PrimaryFails_SecondarySucceeds_MetadataShowsFallback
// Test: GetResponseAsync_AllProvidersFail_ThrowsProviderExhaustedException
// Test: GetResponseAsync_CircuitOpen_SkipsProvider_TriesNext
// Test: GetResponseAsync_FallbackMetadata_PopulatedCorrectly
// Test: GetResponseAsync_FallbackMetadata_DisabledCapabilities_Populated
// Test: GetStreamingResponseAsync_PrimaryFails_SecondarySucceeds
// Test: GetStreamingResponseAsync_MidStreamFailure_RetriesFromScratch
// Test: GetStreamingResponseAsync_AllFail_ThrowsProviderExhaustedException
// Test: Dispose_DisposesAllProviderClients
```

```csharp
// File: src/Content/Tests/Infrastructure.AI.Tests/Resilience/PollyProviderHealthMonitorTests.cs
// These tests validate the contract defined by IProviderHealthMonitor:

// Test: GetProviderHealth_CircuitClosed_ReturnsHealthy
// Test: GetProviderHealth_CircuitHalfOpen_ReturnsDegraded
// Test: GetProviderHealth_CircuitOpen_ReturnsUnavailable
// Test: GetAllProviderHealth_ReturnsAllProviders
// Test: IsAnyProviderHealthy_AllOpen_ReturnsFalse
// Test: IsAnyProviderHealthy_OneClosed_ReturnsTrue
// Test: OnCircuitStateChanged_Fires_OnTransition
```

```csharp
// File: src/Content/Tests/Application.Core.Tests/ (or Infrastructure.AI.Tests)
// Integration tests validating factory integration:

// Test: CreateContext_ResilienceEnabled_UsesResilientProvider
// Test: CreateContext_ResilienceDisabled_UsesOriginalFactory
// Test: CreateContext_ResilientProviderNotRegistered_FallsBackToFactory
```

---

## Implementation Details

### File 1: `IResilientChatClientProvider`

**Path:** `src/Content/Application/Application.AI.Common/Interfaces/Resilience/IResilientChatClientProvider.cs`

**Purpose:** Returns a pre-composed `IChatClient` that wraps the full provider fallback chain with per-provider Polly resilience pipelines. This is a **separate interface** from `IChatClientFactory` -- it does not decorate or extend the existing factory. The existing `IChatClientFactory` contract is "give me a client for this specific provider" while `IResilientChatClientProvider` means "give me a client spanning multiple providers with fallback."

**Namespace:** `Application.AI.Common.Interfaces.Resilience`

**Interface signature:**

```csharp
/// <summary>
/// Provides a pre-composed <see cref="IChatClient"/> that wraps the configured
/// provider fallback chain with per-provider resilience pipelines (retry, circuit
/// breaker, timeout). The returned client is transparent to consumers -- it
/// implements <see cref="IChatClient"/> and attaches <see cref="FallbackMetadata"/>
/// to responses when fallback occurs.
/// </summary>
/// <remarks>
/// <para>
/// This is intentionally separate from <see cref="IChatClientFactory"/>. The factory
/// contract is "give me a client for a specific provider + deployment." This contract
/// is "give me a single client that spans all configured providers with automatic
/// fallback and resilience." These are fundamentally different operations.
/// </para>
/// <para>
/// When <c>ResilienceConfig.Enabled</c> is false, the implementation returns the
/// primary provider's raw client directly (no Polly wrapping, no fallback chain).
/// </para>
/// </remarks>
public interface IResilientChatClientProvider
{
    /// <summary>
    /// Returns a resilient chat client wrapping the full provider fallback chain.
    /// The result is cached -- the provider chain does not change at runtime.
    /// </summary>
    Task<IChatClient> GetResilientChatClientAsync(CancellationToken ct = default);
}
```

**Key design decisions:**
- Returns `Task<IChatClient>` (not a custom type) so consumers are unaware of resilience wrapping. The `AgentExecutionContextFactory` can use it as a drop-in replacement.
- Single method -- no provider selection, no configuration leaking through the interface. The implementation reads `ResilienceConfig.FallbackChain` internally.
- Result is cached. The fallback chain is static for the lifetime of the process. Config changes require restart (consistent with how `IChatClientFactory` works today).
- Uses `Microsoft.Extensions.AI.IChatClient` -- the same type already referenced throughout `Application.AI.Common`.

**Required using directives:**
- `Microsoft.Extensions.AI` (for `IChatClient`)
- `Domain.AI.Resilience` (for `FallbackMetadata` reference in XML docs)

---

### File 2: `IProviderHealthMonitor`

**Path:** `src/Content/Application/Application.AI.Common/Interfaces/Resilience/IProviderHealthMonitor.cs`

**Purpose:** Exposes circuit breaker state for all configured LLM providers. Consumed by OTel gauge updates, the retry queue drain trigger, dashboard health display, and pre-flight checks.

**Namespace:** `Application.AI.Common.Interfaces.Resilience`

**Interface signature:**

```csharp
/// <summary>
/// Exposes circuit breaker health state for configured LLM providers. Maps
/// Polly <c>CircuitState</c> to <see cref="ProviderHealthState"/>: Closed = Healthy,
/// HalfOpen = Degraded, Open/Isolated = Unavailable.
/// </summary>
/// <remarks>
/// <para>
/// No synthetic pre-warm probes are used. LLM API calls cost tokens and there is
/// no lightweight health endpoint. Recovery detection relies on Polly's built-in
/// half-open behavior: when a circuit transitions to HalfOpen, the next real request
/// serves as the recovery probe.
/// </para>
/// <para>
/// The <see cref="OnCircuitStateChanged"/> event fires on every state transition,
/// enabling OTel gauge updates and retry queue drain triggers without polling.
/// </para>
/// </remarks>
public interface IProviderHealthMonitor
{
    /// <summary>
    /// Gets the current health state for a specific provider.
    /// Returns <see cref="ProviderHealthState.Healthy"/> if the provider is unknown.
    /// </summary>
    ProviderHealthState GetProviderHealth(string providerName);

    /// <summary>
    /// Gets the current health state for all configured providers.
    /// </summary>
    IReadOnlyDictionary<string, ProviderHealthState> GetAllProviderHealth();

    /// <summary>
    /// Returns true if at least one provider is in the <see cref="ProviderHealthState.Healthy"/> state.
    /// Used by the retry queue to determine if drain is possible.
    /// </summary>
    bool IsAnyProviderHealthy();

    /// <summary>
    /// Raised when any provider's circuit breaker changes state. The callback receives
    /// the provider name and the new <see cref="ProviderHealthState"/>.
    /// </summary>
    event Action<string, ProviderHealthState>? OnCircuitStateChanged;
}
```

**Key design decisions:**
- `event Action<string, ProviderHealthState>?` is used instead of a delegate type or `EventHandler<T>` to keep it simple and avoid allocating `EventArgs` subclasses. The nullable event matches the C# convention where the event may have zero subscribers.
- `GetProviderHealth` returns `Healthy` for unknown providers rather than throwing. This follows the "assume full capability if not declared" pattern from the capability registry (section-14) and avoids defensive null checks at every call site.
- `IsAnyProviderHealthy()` is a convenience method that prevents callers from needing to enumerate `GetAllProviderHealth()`. The retry queue (section-15) uses this on every drain cycle.
- No `RegisterProvider` or `SetState` methods on the interface -- the implementation (`PollyProviderHealthMonitor` in section-13) receives `CircuitBreakerStateProvider` references during construction and manages state internally.

**Required using directives:**
- `Domain.AI.Resilience` (for `ProviderHealthState`)

---

## Conventions and Patterns

Follow these conventions observed in the existing codebase:

1. **Namespace matches folder path:** `Application.AI.Common.Interfaces.Resilience` for files in `Interfaces/Resilience/`.
2. **Full XML documentation on all public types and members.** This is a template -- docs are teaching material. See `IChatClientFactory.cs` and `IAutonomyTierResolver.cs` for the expected level of detail.
3. **One interface per file.** Each interface gets its own `.cs` file.
4. **No implementation in this section.** These are pure interface definitions. Implementations come in sections 13 and 16.
5. **Domain type references only.** Both interfaces reference only `Domain.AI.Resilience` types (from section-02) and `Microsoft.Extensions.AI`. No infrastructure types (Polly, etc.) leak into the application layer.

---

## File Checklist

| File | Action | Project |
|------|--------|---------|
| `src/Content/Application/Application.AI.Common/Interfaces/Resilience/IResilientChatClientProvider.cs` | Create | Application.AI.Common |
| `src/Content/Application/Application.AI.Common/Interfaces/Resilience/IProviderHealthMonitor.cs` | Create | Application.AI.Common |

No `.csproj` modifications needed -- `Application.AI.Common` already references `Domain.AI` and `Microsoft.Extensions.AI`.

---

## Implementation Notes

**Status:** Complete
**Commit:** (see git log)

### Deviations from Plan
- None. Implementation matches plan exactly.
- Code review auto-fix: added `using Domain.AI.Resilience;` to `IResilientChatClientProvider.cs` and shortened the fully-qualified `FallbackMetadata` cref to use the imported namespace, matching codebase convention.

### Files Created
- `src/Content/Application/Application.AI.Common/Interfaces/Resilience/IResilientChatClientProvider.cs`
- `src/Content/Application/Application.AI.Common/Interfaces/Resilience/IProviderHealthMonitor.cs`

### Test Results
- No tests in this section (pure interface definitions). Tests validating these contracts are in sections 13, 16, and 21.
