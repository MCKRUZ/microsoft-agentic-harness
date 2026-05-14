# Section 12: Resilient Chat Client

## Overview

This section implements `ResilientChatClient`, the `IChatClient` wrapper that iterates through an ordered provider fallback chain, executing each through its own per-provider Polly resilience pipeline. It is the core runtime component of the fallback subsystem -- consumers call `GetResponseAsync` or `GetStreamingResponseAsync` and the class transparently handles retries, circuit breaker tripping, provider failover, and `FallbackMetadata` attachment.

`ResilientChatClient` does **not** use Polly's built-in `FallbackStrategy`. The fallback across providers is a simple iteration loop because each provider has its own distinct resilience pipeline and the loop needs to collect failure metadata across providers. Polly fallback is designed for single-provider retry/fallback scenarios.

## Dependencies

- **section-02-domain-resilience**: `FallbackMetadata` record attached to responses, `ProviderExhaustedException` thrown when all providers fail, `ProviderHealthState` enum for circuit state snapshots.
- **section-07-resilience-interfaces**: `IProviderHealthMonitor` used to snapshot circuit states into `FallbackMetadata.CircuitStates` and to check if a provider should be skipped (circuit open). `IResilientChatClientProvider` is the interface that section-16 implements -- this section builds the client that provider returns.
- **section-11-polly-pipelines**: `ProviderResiliencePipelineBuilder` produces the `ResiliencePipeline<ChatResponse>` instances passed to `ResilientChatClient`'s constructor. Each provider gets its own independent pipeline with retry + circuit breaker + timeout.
- **section-14-capability-registry** (optional at construction time): `ProviderCapabilityRegistry` provides capability diffing to populate `FallbackMetadata.DisabledCapabilities`. If not available, disabled capabilities will be an empty set.

## Downstream Consumers

- **section-16-resilient-provider**: `ResilientChatClientProvider` composes and returns `ResilientChatClient` instances.
- **section-17-governance-integration**: Indirectly -- agents using the resilient client get transparent fallback.

---

## Tests First

**File:** `src/Content/Tests/Infrastructure.AI.Tests/Resilience/ResilientChatClientTests.cs`

All tests use mock `IChatClient` instances (either `FakeChatClient` from the existing test infrastructure, or Moq-based mocks that throw on demand). `ResiliencePipeline<ChatResponse>` can be constructed using `ResiliencePipeline<ChatResponse>.Empty` for tests that don't need resilience behavior, or with a configured pipeline for circuit breaker tests.

```csharp
namespace Infrastructure.AI.Tests.Resilience;

/// <summary>
/// Tests for ResilientChatClient -- the IChatClient wrapper that iterates through
/// a provider fallback chain with per-provider resilience pipelines.
/// Uses FakeChatClient instances and Polly test utilities.
/// </summary>
public sealed class ResilientChatClientTests : IDisposable
{
    // Helper: Create a ProviderEntry with a given name, IChatClient, and optional ResiliencePipeline.
    // Helper: Create test messages (single user message).
    // Helper: Extract FallbackMetadata from ChatResponse.AdditionalProperties.

    // Test: GetResponseAsync_PrimarySucceeds_NoFallback_MetadataShowsPrimary
    //   Arrange: Two providers -- primary returns success, secondary never called.
    //            Both use ResiliencePipeline<ChatResponse>.Empty.
    //   Act: Call GetResponseAsync with a simple user message.
    //   Assert: Response comes from primary. FallbackMetadata.IsFallback == false.
    //           FallbackMetadata.ActiveProvider == primary name.
    //           FallbackMetadata.FailedProviders is empty.
    //           Secondary client's RequestHistory is empty (never called).

    // Test: GetResponseAsync_PrimaryFails_SecondarySucceeds_MetadataShowsFallback
    //   Arrange: Primary throws HttpRequestException on GetResponseAsync.
    //            Secondary returns success. Both use Empty pipeline.
    //   Act: Call GetResponseAsync.
    //   Assert: Response comes from secondary.
    //           FallbackMetadata.IsFallback == true.
    //           FallbackMetadata.ActiveProvider == secondary name.
    //           FallbackMetadata.FailedProviders contains primary name.

    // Test: GetResponseAsync_AllProvidersFail_ThrowsProviderExhaustedException
    //   Arrange: Both primary and secondary throw HttpRequestException.
    //   Act: Call GetResponseAsync.
    //   Assert: Throws ProviderExhaustedException.
    //           ProviderExhaustedException.FailedProviders contains both names.
    //           ProviderExhaustedException.RetryAfter > TimeSpan.Zero.

    // Test: GetResponseAsync_CircuitOpen_SkipsProvider_TriesNext
    //   Arrange: Primary's health monitor reports Unavailable (circuit open).
    //            Secondary returns success.
    //   Act: Call GetResponseAsync.
    //   Assert: Primary is skipped (not called). Secondary serves.
    //           FallbackMetadata.FailedProviders contains primary name.

    // Test: GetResponseAsync_FallbackMetadata_PopulatedCorrectly
    //   Arrange: Three providers -- first two fail, third succeeds.
    //   Act: Call GetResponseAsync.
    //   Assert: FallbackMetadata.ActiveProvider == third name.
    //           FallbackMetadata.FailedProviders == [first, second] in order.
    //           FallbackMetadata.CircuitStates contains all three providers.

    // Test: GetResponseAsync_FallbackMetadata_DisabledCapabilities_Populated
    //   Arrange: Primary supports vision. Fallback does not.
    //            Primary fails, fallback succeeds.
    //            ProviderCapabilityRegistry returns diff.
    //   Act: Call GetResponseAsync.
    //   Assert: FallbackMetadata.DisabledCapabilities contains "vision".

    // Test: GetStreamingResponseAsync_PrimaryFails_SecondarySucceeds
    //   Arrange: Primary throws on GetStreamingResponseAsync call.
    //            Secondary returns async enumerable with chunks.
    //   Act: Call GetStreamingResponseAsync, collect all chunks.
    //   Assert: Chunks come from secondary. Primary was attempted first.

    // Test: GetStreamingResponseAsync_MidStreamFailure_RetriesFromScratch
    //   Arrange: Primary starts streaming, yields 2 chunks, then throws.
    //            Secondary returns complete stream.
    //   Act: Call GetStreamingResponseAsync, collect all chunks.
    //   Assert: All chunks come from secondary (partial primary chunks discarded).
    //           No primary chunks appear in the final output.

    // Test: GetStreamingResponseAsync_AllFail_ThrowsProviderExhaustedException
    //   Arrange: Both providers throw on streaming.
    //   Act: Enumerate GetStreamingResponseAsync.
    //   Assert: Throws ProviderExhaustedException.

    // Test: Dispose_DisposesAllProviderClients
    //   Arrange: Two providers with mock IChatClients that track Dispose calls.
    //   Act: Call Dispose on ResilientChatClient.
    //   Assert: Both inner clients' Dispose was called.
}
```

---

## Implementation Details

### File: `ResilientChatClient`

**Path:** `src/Content/Infrastructure/Infrastructure.AI/Resilience/ResilientChatClient.cs`

**Namespace:** `Infrastructure.AI.Resilience`

**Project:** `Infrastructure.AI`

This class implements `IChatClient` and is the primary artifact of this section. It wraps an ordered list of provider entries and iterates through them on each call, using per-provider resilience pipelines.

#### Internal Types

**`ProviderEntry`** -- a record or readonly struct holding the data for one provider in the chain:

- `Name` (`string`) -- provider identifier (e.g., `"azure-openai"`, `"anthropic"`), used in logging, metrics, and `FallbackMetadata`
- `Client` (`IChatClient`) -- the actual provider client from `IChatClientFactory`
- `Pipeline` (`ResiliencePipeline<ChatResponse>`) -- the per-provider Polly pipeline from `ProviderResiliencePipelineBuilder` (section-11)

This type can be a nested `internal sealed record` inside `ResilientChatClient` or a standalone internal record in the same namespace. Keep it internal -- consumers never see it.

#### Constructor

```csharp
/// <summary>
/// Creates a resilient chat client wrapping an ordered provider fallback chain.
/// </summary>
/// <param name="providers">Ordered provider entries. First is primary, rest are fallbacks.</param>
/// <param name="healthMonitor">Provides circuit breaker health state for skip-on-open logic.</param>
/// <param name="capabilityRegistry">Optional. Diffs capabilities between primary and active provider.</param>
/// <param name="logger">Optional logger.</param>
public ResilientChatClient(
    IReadOnlyList<ProviderEntry> providers,
    IProviderHealthMonitor healthMonitor,
    ProviderCapabilityRegistry? capabilityRegistry = null,
    ILogger<ResilientChatClient>? logger = null)
```

Store `providers` as a readonly field. Validate non-empty list in constructor (throw `ArgumentException` if empty). The first entry is the primary provider.

#### `GetResponseAsync` -- Provider Iteration Loop

The core algorithm:

1. Initialize `failedProviders` as an empty list of strings.
2. Iterate through `_providers` in order.
3. For each provider:
   a. Check `_healthMonitor.GetProviderHealth(provider.Name)`. If `Unavailable`, add to `failedProviders`, log at Debug, skip to next.
   b. Execute `provider.Pipeline.ExecuteAsync(async ct => await provider.Client.GetResponseAsync(messages, options, ct), cancellationToken)`.
   c. On success: build `FallbackMetadata`, attach to `response.AdditionalProperties`, return response.
   d. On exception (any -- the pipeline handles retry internally; if it throws, the provider is exhausted): add provider name to `failedProviders`, log at Warning, continue to next provider.
4. If no provider succeeded: throw `ProviderExhaustedException(failedProviders, retryAfter)` where `retryAfter` is derived from config (circuit breaker break duration) or a default like 60 seconds.

**FallbackMetadata construction** after a successful response:

```csharp
var metadata = new FallbackMetadata
{
    ActiveProvider = successfulProvider.Name,
    IsFallback = failedProviders.Count > 0,
    FailedProviders = failedProviders.AsReadOnly(),
    DisabledCapabilities = GetDisabledCapabilities(successfulProvider.Name),
    CircuitStates = _healthMonitor.GetAllProviderHealth()
};
```

**Attaching metadata to response:** Use a well-known key in `ChatResponse.AdditionalProperties`:

```csharp
internal const string FallbackMetadataKey = "FallbackMetadata";

// After building metadata:
response.AdditionalProperties ??= new AdditionalPropertiesDictionary();
response.AdditionalProperties[FallbackMetadataKey] = metadata;
```

The `AdditionalPropertiesDictionary` type from `Microsoft.Extensions.AI` is the standard way to attach extra data to responses. Downstream consumers (telemetry, dashboard) can retrieve it with:

```csharp
if (response.AdditionalProperties?.TryGetValue(ResilientChatClient.FallbackMetadataKey, out var raw) == true
    && raw is FallbackMetadata metadata) { ... }
```

#### `GetStreamingResponseAsync` -- Streaming Fallback

Streaming adds complexity because `IAsyncEnumerable<ChatResponseUpdate>` is lazy. The resilience pipeline cannot wrap the entire enumeration -- it wraps the *initiation* of the stream.

Algorithm:

1. Same provider iteration loop as `GetResponseAsync`.
2. For each provider:
   a. Skip if circuit is open (same health check).
   b. Initiate the stream: call `provider.Client.GetStreamingResponseAsync(messages, options, ct)`.
   c. Try to yield the first chunk. If the first `MoveNextAsync()` throws, the initiation failed -- catch, add to failed providers, try next provider.
   d. If first chunk succeeds, continue yielding remaining chunks. If a **mid-stream failure** occurs (exception during subsequent `MoveNextAsync()`), discard all chunks from this provider, add to failed providers, try next provider from scratch.
   e. If streaming completes: done.
3. If no provider succeeds: throw `ProviderExhaustedException`.

**Key design decision on mid-stream failure:** When a provider fails mid-stream, the partial chunks are discarded. The next provider starts a fresh stream. This means the consumer may see no output, then suddenly get the full response from the fallback provider. This is the correct behavior because:
- Partial responses from different providers would be incoherent
- The consumer is iterating an `IAsyncEnumerable` -- there's no way to "un-yield" chunks already returned
- For the calling code pattern (`await foreach`), this means the enumeration restarts internally and the consumer sees a complete response from whichever provider succeeds

**Implementation note on mid-stream discard:** Since `GetStreamingResponseAsync` returns `IAsyncEnumerable<ChatResponseUpdate>`, and we cannot un-yield items, the approach is:

- The method internally buffers nothing. It yields chunks from the active provider as they arrive.
- If mid-stream failure occurs, the method needs to handle this gracefully. Since `IAsyncEnumerable` methods in C# use `yield return`, throwing mid-stream would propagate to the consumer.
- The recommended approach: wrap the entire streaming logic in a method that returns `IAsyncEnumerable<ChatResponseUpdate>`. Use a try/catch around the inner enumeration. On failure, break the inner loop and start with the next provider. The `yield return` from the outer loop continues with the next provider's stream.

```csharp
public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
    IEnumerable<ChatMessage> messages,
    ChatOptions? options = null,
    [EnumeratorCancellation] CancellationToken cancellationToken = default)
{
    var failedProviders = new List<string>();
    
    foreach (var provider in _providers)
    {
        if (_healthMonitor.GetProviderHealth(provider.Name) == ProviderHealthState.Unavailable)
        {
            failedProviders.Add(provider.Name);
            continue;
        }

        var succeeded = false;
        
        // Attempt to stream from this provider
        // On any exception (initiation or mid-stream), catch and try next
        // yield return chunks as they arrive from the successful provider
        // ... (implementation detail)
        
        if (succeeded) yield break;
        failedProviders.Add(provider.Name);
    }
    
    throw new ProviderExhaustedException(failedProviders, DefaultRetryAfter);
}
```

**Caveat on mid-stream retry:** If chunks have already been yielded to the consumer and then the provider fails, those chunks are already consumed. The consumer will then receive chunks from the next provider. This means the consumer could see a partial response from provider A followed by a complete response from provider B. To avoid this, an alternative approach is to buffer the entire stream before yielding, but this defeats the purpose of streaming. The plan explicitly states "partial stream is discarded and next provider starts fresh" -- this means we should **not yield any chunks until the provider's stream completes** when fallback is possible. However, this also defeats streaming.

The practical compromise documented in the plan: "if primary fails before any chunks are emitted, try next provider. If primary fails mid-stream, the partial stream is discarded and next provider starts fresh." Since `yield return` has already given chunks to the consumer, the only way to truly discard is to not yield until you know the provider won't fail. This contradicts streaming's purpose.

**Recommended approach matching the plan's intent:**

For the initial implementation, use a simple try-catch around the stream enumeration. If the provider fails during streaming (whether on initiation or mid-stream), move to the next provider. If chunks were already yielded from the failed provider, those chunks are lost -- the consumer gets whatever the next provider produces. This is acceptable because:
1. Mid-stream LLM failures are rare in practice
2. The alternative (buffering) negates streaming's latency benefits
3. The consumer's agent loop can detect incomplete responses and retry at a higher level

If zero-chunk-leakage is critical, the caller should use `GetResponseAsync` instead of streaming.

#### `GetService` and `Dispose`

```csharp
public object? GetService(Type serviceType, object? serviceKey = null)
{
    // Return metadata about this client if requested
    if (serviceType == typeof(ChatClientMetadata))
        return new ChatClientMetadata(nameof(ResilientChatClient));
    return null;
}

public void Dispose()
{
    foreach (var provider in _providers)
    {
        provider.Client.Dispose();
    }
}
```

#### Helper: `GetDisabledCapabilities`

If `ProviderCapabilityRegistry` was injected, diff the primary provider's capabilities against the active provider's capabilities. Return the set of capability names the active provider lacks compared to the primary.

```csharp
private IReadOnlySet<string> GetDisabledCapabilities(string activeProviderName)
{
    if (_capabilityRegistry is null || _providers.Count == 0)
        return ImmutableHashSet<string>.Empty;
    
    var primaryName = _providers[0].Name;
    if (primaryName == activeProviderName)
        return ImmutableHashSet<string>.Empty;
    
    return _capabilityRegistry.DiffCapabilities(primaryName, activeProviderName);
}
```

This depends on `ProviderCapabilityRegistry` from section-14 having a `DiffCapabilities(string primary, string fallback)` method that returns `IReadOnlySet<string>`.

#### OTel Integration

Use `ResilienceMetrics` (from section-03) to record:
- `ResilienceMetrics.FallbackActivations.Add(1, tag: activeProviderName)` when a fallback occurs
- `ResilienceMetrics.ProviderDuration.Record(elapsed, tag: providerName)` per provider attempt (success or failure)
- `ResilienceMetrics.DegradationEvents.Add(1)` when `ProviderExhaustedException` is thrown

These are fire-and-forget metric calls. They should not affect the control flow or exception handling.

---

## Conventions and Patterns

1. **Class is `sealed`** -- no inheritance intended. Follows the pattern of all other `IChatClient` implementations in the codebase (`EchoChatClient`, `ModelBoundChatClient`).
2. **Constructor validation** -- throw `ArgumentException` for empty provider list. Follow the guard clause patterns in `AgentFactory`.
3. **Logging** -- use `ILogger<ResilientChatClient>` with structured logging. Log provider skip (Debug), provider failure (Warning), all-providers-exhausted (Error). Include provider name and attempt index in log scope.
4. **Thread safety** -- the class is stateless per-call (no mutable instance state beyond the readonly provider list). Multiple concurrent calls are safe. `FallbackMetadata` is constructed per-call.
5. **Namespace:** `Infrastructure.AI.Resilience` -- consistent with all other resilience types in this subsystem.
6. **Full XML documentation** on the class, constructor, and public methods.

---

## File Checklist

| File | Action | Project |
|------|--------|---------|
| `src/Content/Infrastructure/Infrastructure.AI/Resilience/ResilientChatClient.cs` | Create | Infrastructure.AI |
| `src/Content/Tests/Infrastructure.AI.Tests/Resilience/ResilientChatClientTests.cs` | Create | Infrastructure.AI.Tests |

**No `.csproj` modifications needed for this section.** `Infrastructure.AI` already references `Application.AI.Common` (which provides `IProviderHealthMonitor`) and will have `Polly.Core` added in section-11. `Infrastructure.AI.Tests` already references `Infrastructure.AI`, `FluentAssertions`, `Moq`, and `xunit`.

---

## Verification

After implementing, the solution should build and the new tests should pass:

```
dotnet build src/AgenticHarness.slnx
dotnet test src/Content/Tests/Infrastructure.AI.Tests/Infrastructure.AI.Tests.csproj --filter "FullyQualifiedName~ResilientChatClientTests"
```

---

## Implementation Notes

**Deviations from plan:**
- `ProviderEntry` gains a `StreamPipeline` (`ResiliencePipeline`) parameter for stream initiation resilience. Code review identified that streaming bypassed the Polly pipeline entirely — now stream initiation is wrapped in the non-generic pipeline from `BuildForStreamInitiation`.
- `FallbackMetadataKey` is `public` (not `internal`) since downstream consumers need to extract metadata from responses.
- `ProviderEntry` record is `public` (not `internal`) due to C# accessibility rules — the constructor exposes it as a parameter type.
- Added `CancellationToken.ThrowIfCancellationRequested()` at method entry points.
- Fixed null-forgiving `lastException!` — uses conditional construction to handle all-providers-skipped-by-health-monitor case.
- Streaming `ProviderExhaustedException` now includes inner exception (tracks `lastException` through the loop).
- `ProviderCapabilityRegistry` integration deferred — `DisabledCapabilities` returns `ImmutableHashSet<string>.Empty`. Will be wired in section 16.

**Final test count:** 9 (all passing, ~200ms total duration)
- GetResponseAsync: primary success, primary fail/secondary success, all fail, circuit open skip, metadata populated correctly
- GetStreamingResponseAsync: primary fail/secondary success, mid-stream fallback, all fail
- Dispose: disposes all inner clients
