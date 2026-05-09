# Section 02: Domain Resilience Models

## Overview

This section creates the domain-layer value types for the fallback/resilience subsystem. These are pure domain concepts in `Domain.AI/Resilience/` with zero framework dependencies. They define the vocabulary that all resilience infrastructure builds on: provider health state, fallback metadata attached to responses, and the exception thrown when all providers are exhausted.

This section has **no dependencies** on other sections and can be implemented in parallel with section-01 (domain escalation models). It **blocks** sections 03 (OTel conventions), 07 (resilience interfaces), 11 (Polly pipelines), and 12 (resilient chat client).

---

## File Structure

All files are created under:

```
src/Content/Domain/Domain.AI/Resilience/
    ProviderHealthState.cs
    FallbackMetadata.cs
    ProviderExhaustedException.cs
```

---

## Tests First

Tests placed in `src/Content/Tests/Domain.AI.Tests/Resilience/` (domain-level test project, consistent with section-01).

**File:** `src/Content/Tests/Domain.AI.Tests/Resilience/ResilienceDomainModelTests.cs`

```csharp
// Test: FallbackMetadata_NoFallback_IsFallbackFalse
//   Arrange: Create FallbackMetadata with IsFallback = false, ActiveProvider = "primary"
//   Assert: IsFallback is false, FailedProviders is empty

// Test: FallbackMetadata_WithFallback_IsFallbackTrue
//   Arrange: Create FallbackMetadata with IsFallback = true, ActiveProvider = "secondary",
//            FailedProviders = ["primary"]
//   Assert: IsFallback is true, FailedProviders contains "primary"

// Test: FallbackMetadata_DisabledCapabilities_ReflectsProviderDiff
//   Arrange: Create FallbackMetadata with DisabledCapabilities = {"vision", "streaming"}
//   Assert: DisabledCapabilities contains both entries, is IReadOnlySet<string>

// Test: ProviderExhaustedException_ContainsRetryAfterAndFailedProviders
//   Arrange: Create ProviderExhaustedException with RetryAfter = TimeSpan.FromSeconds(60),
//            FailedProviders = ["azure-openai", "anthropic"]
//   Assert: RetryAfter matches, FailedProviders matches, Message is meaningful,
//           exception is serializable
```

These are simple record/exception validation tests -- they confirm immutability contracts and that all properties round-trip correctly.

---

## Implementation Details

### 1. `ProviderHealthState` Enum

**File:** `src/Content/Domain/Domain.AI/Resilience/ProviderHealthState.cs`

**Purpose:** Maps to Polly circuit breaker states using domain vocabulary. Consumers (health monitor, OTel metrics, dashboard) work with this enum instead of coupling to Polly's `CircuitState`.

**Values:**
- `Healthy = 0` -- maps to circuit breaker `Closed` state. Provider is accepting requests normally.
- `Degraded = 1` -- maps to circuit breaker `HalfOpen` state. Provider is being probed for recovery. Next real request serves as the recovery probe (no synthetic health checks -- LLM API calls cost tokens).
- `Unavailable = 2` -- maps to circuit breaker `Open` or `Isolated` state. Provider is not accepting requests; fallback chain skips this provider.

**Convention:** Follow the same pattern as `AutonomyLevel` in `Domain.AI/Governance/AutonomyLevel.cs` -- file-scoped namespace, XML doc comments on every value, numeric ordering enables comparison operators.

### 2. `FallbackMetadata` Record

**File:** `src/Content/Domain/Domain.AI/Resilience/FallbackMetadata.cs`

**Purpose:** Attached to `ChatResponse.AdditionalProperties` (or a well-known extension key) so that consuming code -- the agent harness, telemetry, dashboard -- knows which provider actually served the response and what capabilities were lost during fallback.

**Properties:**
- `ActiveProvider` (`string`, required) -- the provider name that served the response (e.g., `"azure-openai"`, `"anthropic"`)
- `IsFallback` (`bool`, required) -- `true` when the response came from a non-primary provider
- `FailedProviders` (`IReadOnlyList<string>`, required) -- ordered list of providers that failed before `ActiveProvider` succeeded. Empty list when primary succeeds.
- `DisabledCapabilities` (`IReadOnlySet<string>`, required) -- features unavailable on the active provider compared to the primary (e.g., `"tool_calling"`, `"streaming"`, `"vision"`). Populated by `ProviderCapabilityRegistry` (section-14) diffing primary vs. active provider capabilities. Empty set when no capabilities are lost.
- `CircuitStates` (`IReadOnlyDictionary<string, ProviderHealthState>`, required) -- snapshot of all providers' health at the time of the response. Enables telemetry and dashboard to show full chain status.

**Immutability:** Use `init`-only properties with `required` modifier. All collection properties use read-only interfaces. Follow the same `sealed record` pattern as `AutonomyExceededResult` and `GovernanceDecision`.

**Design note:** The record should have no factory methods or computed properties initially -- it is a pure data carrier. The `ResilientChatClient` (section-12) constructs it after iterating the provider chain.

### 3. `ProviderExhaustedException`

**File:** `src/Content/Domain/Domain.AI/Resilience/ProviderExhaustedException.cs`

**Purpose:** Thrown by `ResilientChatClient` (section-12) when every provider in the fallback chain has failed (all circuits open, all retries exhausted). Carries structured failure information so callers can make informed decisions (queue for retry, return degraded response, escalate to human).

**Design:**
- Inherits from `Exception` (not a custom base -- this is genuinely exceptional)
- Constructor takes `IReadOnlyList<string> failedProviders` and `TimeSpan retryAfter`
- `FailedProviders` property (`IReadOnlyList<string>`) -- which providers were attempted
- `RetryAfter` property (`TimeSpan`) -- suggested wait time before retrying, derived from the shortest circuit breaker break duration among all failed providers
- Message auto-generated from failed providers list: `"All LLM providers exhausted: {string.Join(", ", failedProviders)}. Retry after {retryAfter.TotalSeconds}s."`
- Include inner exception constructor overload for wrapping the last provider's exception

**Convention note:** Domain exceptions in this codebase don't use `[Serializable]` attribute or implement `ISerializable` -- the serialization is handled by the structured logging pipeline. Keep it simple.

---

## Relationship to Other Types

These types interact with the following (created in other sections -- reference only, do not implement):

| Type | Section | Relationship |
|------|---------|-------------|
| `ResilienceConventions` | section-03 | OTel metric names reference `ProviderHealthState` values as tag values |
| `ResilienceConfig` | section-04 | Config drives circuit breaker parameters that determine `ProviderHealthState` transitions |
| `IProviderHealthMonitor` | section-07 | Returns `ProviderHealthState` per provider |
| `IResilientChatClientProvider` | section-07 | Constructs clients that produce `FallbackMetadata` |
| `ProviderResiliencePipelineBuilder` | section-11 | Maps Polly circuit states to `ProviderHealthState` |
| `ResilientChatClient` | section-12 | Constructs `FallbackMetadata`, throws `ProviderExhaustedException` |
| `PollyProviderHealthMonitor` | section-13 | Maps Polly `CircuitState` to `ProviderHealthState` |
| `LlmRetryQueue` | section-15 | Catches `ProviderExhaustedException` to queue requests |

---

## Namespace and Project Conventions

- **Namespace:** `Domain.AI.Resilience` (matches folder path under `Domain.AI/`)
- **Project:** `Domain.AI.csproj` -- no new package references needed (these are pure C# records/enums/exceptions)
- **File-scoped namespaces:** Yes, following the pattern in `Domain.AI/Governance/`
- **XML documentation:** Full XML docs on all public types and members -- this is a template project where docs serve as teaching material

---

## Verification

After implementing these three files, the solution should build cleanly:

```
dotnet build src/AgenticHarness.slnx
```

No other files need modification. These are additive, zero-dependency domain types.
