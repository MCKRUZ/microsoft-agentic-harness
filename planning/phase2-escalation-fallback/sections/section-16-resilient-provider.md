# Section 16: Resilient Chat Client Provider

## Overview

`ResilientChatClientProvider` is the implementation of `IResilientChatClientProvider` (defined in section-07). It is the composition root that assembles the entire fallback chain subsystem into a single `IChatClient` instance. It reads `ResilienceConfig.FallbackChain` from the Options pattern, creates raw `IChatClient` instances via `IChatClientFactory`, wraps each in a Polly resilience pipeline via `ProviderResiliencePipelineBuilder`, wires up `PollyProviderHealthMonitor` with circuit breaker state providers, and composes everything into a `ResilientChatClient`.

The result is cached -- the fallback chain is static for the lifetime of the process. Config changes require restart, consistent with how `IChatClientFactory` works today. When `ResilienceConfig.Enabled` is `false`, the provider returns the primary provider's raw client directly with no Polly wrapping and no fallback chain.

This class also integrates with `AgentExecutionContextFactory` -- when resilience is enabled and the provider is registered, the factory uses `IResilientChatClientProvider.GetResilientChatClientAsync()` instead of `IChatClientFactory.GetChatClientAsync()` for agent chat clients.

## Dependencies

| Section | What It Provides |
|---------|-----------------|
| **section-04-config-and-validation** | `ResilienceConfig`, `FallbackProviderConfig` config classes at `Domain.Common/Config/AI/Resilience/` |
| **section-07-resilience-interfaces** | `IResilientChatClientProvider` interface at `Application.AI.Common/Interfaces/Resilience/` |
| **section-11-polly-pipelines** | `ProviderResiliencePipelineBuilder.Build()` which returns `ResiliencePipeline<ChatResponse>` and outputs `CircuitBreakerStateProvider` |
| **section-12-resilient-chat-client** | `ResilientChatClient` class and its inner `ProviderEntry` type at `Infrastructure.AI/Resilience/` |
| **section-13-health-monitor** | `PollyProviderHealthMonitor` (concrete type with `ReportStateChange`) at `Infrastructure.AI/Resilience/` |
| **section-14-capability-registry** | `ProviderCapabilityRegistry` at `Infrastructure.AI/Resilience/` for capability diffing |

Additionally depends on the existing `IChatClientFactory` interface (`Application.AI.Common/Interfaces/IChatClientFactory.cs`) which creates raw `IChatClient` instances per provider deployment.

## Downstream Consumers

- **section-17-governance-integration**: Agents using the resilient client get transparent fallback behavior.
- **section-19-di-registration**: Registers `ResilientChatClientProvider` as `IResilientChatClientProvider` (singleton).

---

## Tests First

### File: `src/Content/Tests/Infrastructure.AI.Tests/Resilience/ResilientChatClientProviderTests.cs`

Tests use Moq for `IChatClientFactory`, `IOptionsMonitor<ResilienceConfig>`, and the health monitor. The provider's composition logic is the focus -- not the resilience pipelines themselves (those are tested in section-11) or the fallback iteration (tested in section-12).

```csharp
namespace Infrastructure.AI.Tests.Resilience;

/// <summary>
/// Tests for ResilientChatClientProvider -- the composition root that assembles
/// the fallback chain from config, wires resilience pipelines, and returns
/// a ResilientChatClient. Validates config-driven composition, caching, and
/// the disabled-resilience bypass.
/// </summary>
public sealed class ResilientChatClientProviderTests : IAsyncDisposable
{
    // Helper: Create a ResilienceConfig with Enabled=true/false and a FallbackChain
    //         of FallbackProviderConfig entries.
    // Helper: Create a mock IChatClientFactory that returns distinct mock IChatClients
    //         for each (ClientType, DeploymentId) pair.
    // Helper: Create mock IOptionsMonitor<ResilienceConfig> wrapping the config.

    // === Integration with AgentExecutionContextFactory ===

    // Test: CreateContext_ResilienceEnabled_UsesResilientProvider
    //   Arrange: ResilienceConfig.Enabled = true with 2-provider FallbackChain.
    //            Register IResilientChatClientProvider in a test IServiceProvider.
    //            Create AgentExecutionContextFactory with all dependencies.
    //   Act:     Observe that when resilience is enabled, the factory would resolve
    //            the chat client from IResilientChatClientProvider rather than
    //            IChatClientFactory. (This test validates the provider returns a
    //            ResilientChatClient wrapping both providers.)
    //   Assert:  GetResilientChatClientAsync returns non-null IChatClient.
    //            The returned client is a ResilientChatClient (or wraps the chain).

    // Test: CreateContext_ResilienceDisabled_UsesOriginalFactory
    //   Arrange: ResilienceConfig.Enabled = false.
    //   Act:     Call GetResilientChatClientAsync.
    //   Assert:  Returns the primary provider's raw IChatClient directly.
    //            IChatClientFactory.GetChatClientAsync called once for the primary.
    //            No Polly pipeline wrapping (client is the raw instance).

    // Test: CreateContext_ResilientProviderNotRegistered_FallsBackToFactory
    //   Arrange: Do not register IResilientChatClientProvider.
    //            Only IChatClientFactory is available.
    //   Act:     Resolve IChatClient through the AgentExecutionContextFactory.
    //   Assert:  Chat client comes from IChatClientFactory directly.

    // === Provider composition tests ===

    // Test: GetResilientChatClientAsync_BuildsChainFromConfig
    //   Arrange: Config with 3-provider FallbackChain: azure-openai/gpt-4o,
    //            anthropic/claude-sonnet, azure-openai/gpt-35-turbo.
    //   Act:     Call GetResilientChatClientAsync.
    //   Assert:  IChatClientFactory.GetChatClientAsync called 3 times with
    //            the correct (ClientType, DeploymentId) pairs in order.
    //            Returned client is a ResilientChatClient.

    // Test: GetResilientChatClientAsync_CachesResult
    //   Arrange: Config with 2-provider chain.
    //   Act:     Call GetResilientChatClientAsync twice.
    //   Assert:  IChatClientFactory.GetChatClientAsync called only during first call.
    //            Second call returns same instance (reference equality).

    // Test: GetResilientChatClientAsync_EmptyChain_ThrowsInvalidOperation
    //   Arrange: Config with Enabled=true but FallbackChain = empty.
    //   Act:     Call GetResilientChatClientAsync.
    //   Assert:  Throws InvalidOperationException with message about empty chain.

    // Test: GetResilientChatClientAsync_FactoryFailure_PropagatesException
    //   Arrange: IChatClientFactory throws on GetChatClientAsync for one provider.
    //   Act:     Call GetResilientChatClientAsync.
    //   Assert:  Exception propagates (provider that can't be created is a fatal config error).
}
```

### File: `src/Content/Tests/Application.Core.Tests/Factories/AgentExecutionContextFactoryResilienceTests.cs`

These tests specifically validate the `AgentExecutionContextFactory` integration point where it chooses between `IResilientChatClientProvider` and `IChatClientFactory`.

```csharp
namespace Application.Core.Tests.Factories;

/// <summary>
/// Tests for AgentExecutionContextFactory's resilience integration.
/// Validates that the factory correctly selects between IResilientChatClientProvider
/// and IChatClientFactory based on registration and config state.
/// </summary>
public sealed class AgentExecutionContextFactoryResilienceTests
{
    // Test: MapToAgentContextAsync_ResilientProviderAvailable_UsesResilientClient
    //   Arrange: Wire up factory with mocked IResilientChatClientProvider that
    //            returns a mock IChatClient. SkillDefinition with valid config.
    //   Act:     Call MapToAgentContextAsync.
    //   Assert:  The context's chat client resolution path goes through
    //            IResilientChatClientProvider, not IChatClientFactory.

    // Test: MapToAgentContextAsync_ResilientProviderNull_FallsBackToFactory
    //   Arrange: Factory constructed without IResilientChatClientProvider
    //            (null optional dependency). IChatClientFactory available.
    //   Act:     Call MapToAgentContextAsync.
    //   Assert:  Chat client resolved through IChatClientFactory as before.
}
```

---

## Implementation Details

### File 1: `ResilientChatClientProvider`

**Path:** `src/Content/Infrastructure/Infrastructure.AI/Resilience/ResilientChatClientProvider.cs`

**Namespace:** `Infrastructure.AI.Resilience`

**Lifetime:** Singleton (registered in section-19)

#### Constructor Dependencies

```csharp
/// <summary>
/// Composes a <see cref="ResilientChatClient"/> from the configured fallback chain.
/// Reads provider entries from <see cref="ResilienceConfig.FallbackChain"/>, creates
/// raw <see cref="IChatClient"/> instances via <see cref="IChatClientFactory"/>,
/// wraps each in a per-provider Polly resilience pipeline, and caches the result.
/// </summary>
/// <remarks>
/// <para>
/// When <see cref="ResilienceConfig.Enabled"/> is false, returns the primary provider's
/// raw client directly -- no Polly wrapping, no fallback chain, no overhead.
/// </para>
/// <para>
/// The composed client is cached (lazy-initialized). The fallback chain is static for
/// the process lifetime. Config changes require restart.
/// </para>
/// </remarks>
public sealed class ResilientChatClientProvider : IResilientChatClientProvider
{
    public ResilientChatClientProvider(
        IChatClientFactory chatClientFactory,
        IOptionsMonitor<ResilienceConfig> resilienceConfig,
        PollyProviderHealthMonitor healthMonitor,
        ProviderCapabilityRegistry capabilityRegistry,
        ILogger<ResilientChatClientProvider> logger);
}
```

**Key points about constructor dependencies:**

- `IChatClientFactory` -- the existing factory that creates raw per-provider clients. Called once per provider during chain composition.
- `IOptionsMonitor<ResilienceConfig>` -- reads `FallbackChain`, `CircuitBreaker`, `Retry`, `Timeout` config sections.
- `PollyProviderHealthMonitor` -- the **concrete type** (not `IProviderHealthMonitor` interface) because the provider needs to call `ReportStateChange` when wiring Polly callbacks. Section-19 registers this as a dual registration (both concrete and interface resolve to the same singleton).
- `ProviderCapabilityRegistry` -- passed to `ResilientChatClient` for capability diffing.
- `ILogger<ResilientChatClientProvider>` -- logs chain composition at `Information`, individual provider creation at `Debug`.

#### Caching Strategy

Use `Lazy<Task<IChatClient>>` or `SemaphoreSlim` + a nullable field to ensure the chain is composed exactly once even under concurrent access. The `Lazy<Task<IChatClient>>` pattern is cleanest:

```csharp
private readonly Lazy<Task<IChatClient>> _cachedClient;

public ResilientChatClientProvider(/* deps */)
{
    _cachedClient = new Lazy<Task<IChatClient>>(ComposeChainAsync);
}

public Task<IChatClient> GetResilientChatClientAsync(CancellationToken ct = default)
{
    return _cachedClient.Value;
}
```

The `CancellationToken` parameter is not threaded into `Lazy<Task>` creation -- this is acceptable because chain composition happens once at startup and should not be cancellable (a cancelled composition would leave the provider in a broken state).

#### `ComposeChainAsync` -- Chain Composition Logic

This is the core method. Algorithm:

1. Read `ResilienceConfig` from options monitor.
2. **If `Enabled == false`:** Create only the primary provider's raw client via `IChatClientFactory.GetChatClientAsync(chain[0].ClientType, chain[0].DeploymentId)` and return it directly. No Polly wrapping.
3. **If `FallbackChain` is empty:** Throw `InvalidOperationException("ResilienceConfig.Enabled is true but FallbackChain is empty. Configure at least one provider.")`. The FluentValidation validator (section-04) should catch this at startup, but this is a runtime safety net.
4. **For each `FallbackProviderConfig` in `FallbackChain`:**
   a. Call `IChatClientFactory.GetChatClientAsync(entry.ClientType, entry.DeploymentId)` to get the raw `IChatClient`.
   b. Call `ProviderResiliencePipelineBuilder.Build(deploymentId, config, out var stateProvider, logger)` to build the per-provider resilience pipeline. The `deploymentId` serves as the provider name for logging and metrics.
   c. Wire `stateProvider` into `PollyProviderHealthMonitor` -- the Polly pipeline's `OnOpened`/`OnClosed`/`OnHalfOpened` callbacks (registered during `Build`) call `healthMonitor.ReportStateChange()` directly.
   d. Create a `ResilientChatClient.ProviderEntry` with the deployment ID, raw client, and resilience pipeline.
5. Construct `ResilientChatClient` with the ordered list of `ProviderEntry` instances, the `IProviderHealthMonitor` (upcast from the concrete `PollyProviderHealthMonitor`), and the `ProviderCapabilityRegistry`.
6. Log at `Information`: `"Composed resilient chat client with {Count} providers: {ProviderNames}"`.
7. Return the `ResilientChatClient`.

**Important nuance on health monitor wiring:** The `ProviderResiliencePipelineBuilder.Build()` method (section-11) already registers `OnOpened`/`OnClosed`/`OnHalfOpened` callbacks. However, those callbacks need a reference to the `PollyProviderHealthMonitor` to call `ReportStateChange`. There are two approaches:

- **Option A (recommended):** Extend the `Build` method signature to accept an `Action<ProviderHealthState>` callback parameter. The provider creates a closure `state => healthMonitor.ReportStateChange(deploymentId, state)` and passes it. This avoids coupling the pipeline builder to the concrete monitor type.
- **Option B:** Pass `PollyProviderHealthMonitor` directly to `Build`. This works but couples the static builder to a specific monitor implementation.

Pseudocode for the wiring:

```csharp
// Inside ComposeChainAsync, for each provider entry:
var pipeline = ProviderResiliencePipelineBuilder.Build(
    providerName: entry.DeploymentId,
    config: config,
    out var stateProvider,
    onCircuitStateChanged: newState => _healthMonitor.ReportStateChange(entry.DeploymentId, newState),
    logger: _logger);
```

#### Disabled Resilience Path

When `ResilienceConfig.Enabled == false`, the method:
1. Reads the first entry of `FallbackChain` (or uses the framework default from `AppConfig.AI.AgentFramework` if chain is empty).
2. Creates a single raw `IChatClient` via `IChatClientFactory`.
3. Returns it directly -- no `ResilientChatClient` wrapping.

This means consumers get zero overhead when resilience is opted out.

### File 2: `AgentExecutionContextFactory` Modification

**Path:** `src/Content/Application/Application.AI.Common/Factories/AgentExecutionContextFactory.cs`

**Action:** Modify (add optional `IResilientChatClientProvider` dependency)

#### Changes Required

1. **Add constructor parameter:**

```csharp
private readonly IResilientChatClientProvider? _resilientChatClientProvider;

public AgentExecutionContextFactory(
    /* existing params */
    IResilientChatClientProvider? resilientChatClientProvider = null)
{
    /* existing assignments */
    _resilientChatClientProvider = resilientChatClientProvider;
}
```

The parameter is **nullable and optional** (has `= null` default) to maintain backward compatibility.

2. **Add chat client resolution to `MapToAgentContextAsync`:**

```csharp
// After building the context, before returning:
if (_resilientChatClientProvider is not null)
{
    var resilientClient = await _resilientChatClientProvider.GetResilientChatClientAsync(ct);
    additionalProps["__resilientChatClient"] = resilientClient;
}
```

The `CreateFromDelegation` method does NOT need the resilient provider because delegated agents use the same provider chain as their parent (the `ResilientChatClient` is process-scoped, not agent-scoped).

---

## Conventions and Patterns

1. **Class is `sealed`** -- no inheritance intended. Singleton lifetime.
2. **`Lazy<Task<IChatClient>>`** for thread-safe lazy initialization of the composed chain.
3. **Full XML documentation** on the class, constructor, and public methods.
4. **Logging:** `Information` for chain composed, `Debug` for individual provider creation, `Warning` for resilience disabled.
5. **Error handling:** Provider creation failures propagate as-is. A provider that cannot be created is a fatal startup error.
6. **Namespace:** `Infrastructure.AI.Resilience`

---

## Design Decisions

1. **Concrete `PollyProviderHealthMonitor` in constructor, not `IProviderHealthMonitor`.** The provider needs to call `ReportStateChange()` which is on the concrete type, not the interface.

2. **`Lazy<Task<IChatClient>>` with `PublicationOnly` mode.** The `Lazy<Task>` pattern provides exactly-once execution semantics. `PublicationOnly` avoids permanently caching faulted tasks from transient factory errors.

3. **Raw client returned when disabled.** No `ResilientChatClient` wrapper when resilience is off. This eliminates all overhead.

4. **Optional `IResilientChatClientProvider` in factory.** Making it nullable with a default means the factory works unchanged when resilience is not configured.

5. **`DeploymentId` as provider name.** Each `FallbackProviderConfig` uses `DeploymentId` as the provider identity in logging, metrics, and health monitoring.

6. **Separate CircuitBreakerStateProvider per pipeline.** Polly v8 does not allow reusing a `CircuitBreakerStateProvider` across multiple circuit breaker strategies. Each provider gets independent typed and stream state providers.

7. **Conditional metric recording in pipeline builder.** When `onCircuitStateChanged` callback is wired, builder skips its own `RecordCircuitOpened`/`RecordCircuitClosed` to avoid double-counting with the health monitor.

---

## Deviations from Plan

1. **`ProviderCapabilityRegistry` removed from constructor.** Plan listed it as a dependency, but `ResilientChatClient` does not accept it. Removed as dead code per code review MEDIUM-4.
2. **`ILoggerFactory` added to constructor.** Needed to create a properly-typed `ILogger<ResilientChatClient>` for the composed client. The original plan used a logger cast that would always be null at runtime (code review MEDIUM-1).
3. **`AgentExecutionContextFactoryResilienceTests` deferred.** The factory integration test requires mocking too many dependencies. Deferred to section-21 integration tests.
4. **`ProviderResiliencePipelineBuilder` callback is conditional.** When `onCircuitStateChanged` is null (standalone usage), builder keeps its own metric recording. When non-null, monitor is the single metric source.

---

## File Checklist

| File | Action | Project |
|------|--------|---------|
| `src/Content/Infrastructure/Infrastructure.AI/Resilience/ResilientChatClientProvider.cs` | Created | Infrastructure.AI |
| `src/Content/Infrastructure/Infrastructure.AI/Resilience/ProviderResiliencePipelineBuilder.cs` | Modified -- added `onCircuitStateChanged` callback to `Build` and `BuildForStreamInitiation` | Infrastructure.AI |
| `src/Content/Application/Application.AI.Common/Factories/AgentExecutionContextFactory.cs` | Modified -- added `IResilientChatClientProvider?` optional dependency | Application.AI.Common |
| `src/Content/Tests/Infrastructure.AI.Tests/Resilience/ResilientChatClientProviderTests.cs` | Created (6 tests) | Infrastructure.AI.Tests |

---

## Verification

```
dotnet build src/AgenticHarness.slnx
dotnet test src/Content/Tests/Infrastructure.AI.Tests/Infrastructure.AI.Tests.csproj --filter "FullyQualifiedName~ResilientChatClientProviderTests"
```
