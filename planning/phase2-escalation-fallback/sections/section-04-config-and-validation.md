# Section 04: Configuration and FluentValidation

## Overview

This section creates the configuration POCOs and FluentValidation validators for both the Escalation and Resilience subsystems. These config classes live in `Domain.Common/Config/AI/` following the existing Options pattern hierarchy (`AppConfig.AI.Governance.Escalation` and `AppConfig.AI.Resilience`). The validators live in `Application.Core/Validation/` and are auto-discovered by the existing `services.AddValidatorsFromAssembly(assembly)` call in `Application.Core/DependencyInjection.cs`.

This section also modifies two existing config classes:
- `GovernanceConfig` gains an `Escalation` property
- `AIConfig` gains a `Resilience` property

## Dependencies

- **Section 01 (domain-escalation):** Provides `EscalationPriority`, `EscalationTimeoutAction`, `ApprovalStrategyType`, `EscalationWaitBehavior` enums referenced by `EscalationConfig` and `EscalationPriorityConfig`.
- **Section 02 (domain-resilience):** Provides `ProviderHealthState` enum (not directly referenced by config, but conceptually related).

## File Inventory

**New files to create:**

| File | Layer | Purpose |
|------|-------|---------|
| `src/Content/Domain/Domain.Common/Config/AI/Governance/EscalationConfig.cs` | Domain.Common | Root escalation config POCO |
| `src/Content/Domain/Domain.Common/Config/AI/Governance/EscalationPriorityConfig.cs` | Domain.Common | Per-priority-level config POCO |
| `src/Content/Domain/Domain.Common/Config/AI/Resilience/ResilienceConfig.cs` | Domain.Common | Root resilience config POCO |
| `src/Content/Domain/Domain.Common/Config/AI/Resilience/FallbackProviderConfig.cs` | Domain.Common | Per-provider entry in fallback chain |
| `src/Content/Domain/Domain.Common/Config/AI/Resilience/ProviderCapabilitiesConfig.cs` | Domain.Common | Provider feature declaration |
| `src/Content/Domain/Domain.Common/Config/AI/Resilience/CircuitBreakerConfig.cs` | Domain.Common | Circuit breaker tuning |
| `src/Content/Domain/Domain.Common/Config/AI/Resilience/RetryConfig.cs` | Domain.Common | Retry policy tuning |
| `src/Content/Domain/Domain.Common/Config/AI/Resilience/TimeoutConfig.cs` | Domain.Common | Per-attempt timeout |
| `src/Content/Domain/Domain.Common/Config/AI/Resilience/DegradedModeConfig.cs` | Domain.Common | Retry queue and degraded mode |
| `src/Content/Application/Application.Core/Validation/EscalationConfigValidator.cs` | Application.Core | FluentValidation for EscalationConfig |
| `src/Content/Application/Application.Core/Validation/ResilienceConfigValidator.cs` | Application.Core | FluentValidation for ResilienceConfig |

**Existing files to modify:**

| File | Change |
|------|--------|
| `src/Content/Domain/Domain.Common/Config/AI/GovernanceConfig.cs` | Add `Escalation` property of type `EscalationConfig` |
| `src/Content/Domain/Domain.Common/Config/AI/AIConfig.cs` | Add `Resilience` property of type `ResilienceConfig`, add using for Resilience namespace |

---

## Tests FIRST

All validator tests go in `src/Content/Tests/Application.Core.Tests/Validation/`. The testing pattern follows the existing convention: instantiate validator directly, use `ValidateAsync`, assert with FluentAssertions.

### EscalationConfigValidatorTests

**File:** `src/Content/Tests/Application.Core.Tests/Validation/EscalationConfigValidatorTests.cs`

```csharp
namespace Application.Core.Tests.Validation;

/// <summary>
/// Tests for EscalationConfigValidator.
/// Instantiate validator, build valid/invalid EscalationConfig, assert FluentValidation results.
/// Pattern: CreateValidConfig() helper returns baseline valid config; each test mutates one field.
/// </summary>
public class EscalationConfigValidatorTests
{
    // Test: Validate_ValidConfig_NoErrors
    //   Arrange: CreateValidConfig() with Enabled=true, DefaultTimeoutSeconds=300,
    //            DefaultTimeoutAction=DenyAndEscalate, DefaultApprovalStrategy=AnyOf,
    //            PriorityLevels has entries for each EscalationPriority enum value.
    //   Act: ValidateAsync
    //   Assert: result.IsValid == true, result.Errors empty

    // Test: Validate_NegativeTimeout_HasError
    //   Arrange: CreateValidConfig() with DefaultTimeoutSeconds = -1
    //   Act: ValidateAsync
    //   Assert: result.IsValid == false, error on "DefaultTimeoutSeconds"

    // Test: Validate_ZeroTimeout_Allowed
    //   Arrange: CreateValidConfig() with DefaultTimeoutSeconds = 0
    //   Act: ValidateAsync
    //   Assert: result.IsValid == true (zero is valid for informational-only escalations)

    // Test: Validate_NegativePriorityTimeout_HasError
    //   Arrange: CreateValidConfig() with PriorityLevels["Blocking"].TimeoutSeconds = -5
    //   Act: ValidateAsync
    //   Assert: result.IsValid == false, error on "PriorityLevels[Blocking].TimeoutSeconds"

    // Test: Validate_EmptyPriorityLevels_HasError
    //   Arrange: CreateValidConfig() with PriorityLevels = empty dictionary
    //   Act: ValidateAsync
    //   Assert: result.IsValid == false, error on "PriorityLevels"
}
```

### ResilienceConfigValidatorTests

**File:** `src/Content/Tests/Application.Core.Tests/Validation/ResilienceConfigValidatorTests.cs`

```csharp
namespace Application.Core.Tests.Validation;

/// <summary>
/// Tests for ResilienceConfigValidator.
/// Same pattern: CreateValidConfig() baseline, mutate one field per test.
/// When Enabled=false, most validations are skipped (chain can be empty if disabled).
/// When Enabled=true, FallbackChain must be non-empty and all numeric ranges enforced.
/// </summary>
public class ResilienceConfigValidatorTests
{
    // Test: Validate_ValidConfig_NoErrors
    //   Arrange: Enabled=true, FallbackChain with 2 entries (each has ClientType + DeploymentId),
    //            CircuitBreaker, Retry, Timeout, DegradedMode with valid values.
    //   Assert: IsValid == true

    // Test: Validate_EmptyFallbackChain_HasError
    //   Arrange: Enabled=true, FallbackChain = empty array
    //   Assert: error on "FallbackChain"

    // Test: Validate_NegativeFailureRatio_HasError
    //   Arrange: CircuitBreaker.FailureRatio = -0.1
    //   Assert: error on "CircuitBreaker.FailureRatio"

    // Test: Validate_FailureRatioAboveOne_HasError
    //   Arrange: CircuitBreaker.FailureRatio = 1.5
    //   Assert: error on "CircuitBreaker.FailureRatio"

    // Test: Validate_NegativeTimeout_HasError
    //   Arrange: Timeout.PerAttemptSeconds = -1
    //   Assert: error on "Timeout.PerAttemptSeconds"

    // Test: Validate_ZeroMaxQueueSize_HasError
    //   Arrange: DegradedMode.MaxQueueSize = 0
    //   Assert: error on "DegradedMode.MaxQueueSize"

    // Test: Validate_MissingDeploymentId_HasError
    //   Arrange: FallbackChain entry with DeploymentId = "" or null
    //   Assert: error on "FallbackChain[0].DeploymentId"

    // Test: Validate_DisabledConfig_SkipsChainValidation
    //   Arrange: Enabled=false, FallbackChain = empty
    //   Assert: IsValid == true (no chain required when disabled)

    // Test: Validate_NegativeRetryBaseDelay_HasError
    //   Arrange: Retry.BaseDelaySeconds = -1
    //   Assert: error on "Retry.BaseDelaySeconds"

    // Test: Validate_ZeroMinimumThroughput_HasError
    //   Arrange: CircuitBreaker.MinimumThroughput = 0
    //   Assert: error on "CircuitBreaker.MinimumThroughput"
}
```

---

## Implementation Details

### Escalation Configuration

#### EscalationConfig

**File:** `src/Content/Domain/Domain.Common/Config/AI/Governance/EscalationConfig.cs`
**Namespace:** `Domain.Common.Config.AI.Governance`

This is the root config POCO for the escalation subsystem. It is bound from `AppConfig:AI:Governance:Escalation` in appsettings.json.

Properties:
- `Enabled` (`bool`, default `false`) -- whether the escalation system is active. When disabled, `GovernancePolicyBehavior` treats `RequireApproval` as a denial.
- `DefaultTimeoutSeconds` (`int`, default `300`) -- how long to wait for approver response before timeout action fires.
- `DefaultTimeoutAction` (`EscalationTimeoutAction`, default `DenyAndEscalate`) -- what happens when the timeout expires. References the enum from section-01.
- `DefaultApprovalStrategy` (`ApprovalStrategyType`, default `AnyOf`) -- which strategy to use when a rule doesn't specify one. References the enum from section-01.
- `PriorityLevels` (`Dictionary<string, EscalationPriorityConfig>`, default empty) -- per-priority overrides keyed by `EscalationPriority` name strings ("Informational", "Blocking", "Critical").

XML doc should include the config path hierarchy showing the nesting under `AppConfig.AI.Governance.Escalation`, following the pattern in `RagConfig.cs`.

Use `{ get; set; }` with default `new()` or literal defaults, matching the existing config class style (not `init`-only -- `GovernanceConfig` uses `init` but `RagConfig` and most others use `set`; use `set` for consistency with the majority).

#### EscalationPriorityConfig

**File:** `src/Content/Domain/Domain.Common/Config/AI/Governance/EscalationPriorityConfig.cs`
**Namespace:** `Domain.Common.Config.AI.Governance`

Properties:
- `TimeoutSeconds` (`int`, default `300`) -- override timeout for this priority level.
- `Async` (`bool`, default `false`) -- when true, escalation is non-blocking (informational only). Used for `Informational` priority.
- `EscalateToAll` (`bool`, default `false`) -- when true, notify all approvers simultaneously regardless of strategy ordering. Used for `Critical` priority.

### Resilience Configuration

All resilience config classes use namespace `Domain.Common.Config.AI.Resilience`.

#### ResilienceConfig

**File:** `src/Content/Domain/Domain.Common/Config/AI/Resilience/ResilienceConfig.cs`

Root config for fallback chains and provider resilience. Bound from `AppConfig:AI:Resilience`.

Properties:
- `Enabled` (`bool`, default `false`) -- master toggle. When disabled, `ResilientChatClientProvider` returns the primary provider's raw client, `LlmRetryQueue` hosted service is not registered.
- `FallbackChain` (`FallbackProviderConfig[]`, default empty array) -- ordered list of providers. First is primary, rest are fallbacks.
- `CircuitBreaker` (`CircuitBreakerConfig`, default `new()`)
- `Retry` (`RetryConfig`, default `new()`)
- `Timeout` (`TimeoutConfig`, default `new()`)
- `DegradedMode` (`DegradedModeConfig`, default `new()`)

Include XML doc config hierarchy similar to `RagConfig`:
```
AppConfig.AI.Resilience
+-- FallbackChain[]   -- Ordered provider entries with capabilities
+-- CircuitBreaker    -- Failure ratio, sampling, break duration
+-- Retry             -- Max attempts, backoff
+-- Timeout           -- Per-attempt timeout
+-- DegradedMode      -- Retry queue TTL and max size
```

#### FallbackProviderConfig

**File:** `src/Content/Domain/Domain.Common/Config/AI/Resilience/FallbackProviderConfig.cs`

One entry in the fallback chain. Maps directly to `IChatClientFactory.GetChatClientAsync(clientType, deploymentId)`.

Properties:
- `ClientType` (`AIAgentFrameworkClientType`, default `AzureOpenAI`) -- which provider SDK to use. References the existing enum at `Domain.Common.Config.AI.AIAgentFrameworkClientType`.
- `DeploymentId` (`string`, default `""`) -- model deployment name.
- `Capabilities` (`ProviderCapabilitiesConfig`, default `new()`) -- optional feature declarations.

#### ProviderCapabilitiesConfig

**File:** `src/Content/Domain/Domain.Common/Config/AI/Resilience/ProviderCapabilitiesConfig.cs`

Declares what a provider supports, used by `ProviderCapabilityRegistry` (section-14) for capability diffing.

Properties:
- `SupportsToolCalling` (`bool`, default `true`)
- `SupportsStreaming` (`bool`, default `true`)
- `SupportsVision` (`bool`, default `false`)
- `MaxTokens` (`int`, default `4096`)
- `SupportedMediaTypes` (`IReadOnlyList<string>`, default empty list)

#### CircuitBreakerConfig

**File:** `src/Content/Domain/Domain.Common/Config/AI/Resilience/CircuitBreakerConfig.cs`

Tuning for Polly v8 ratio-based circuit breaker.

Properties:
- `FailureRatio` (`double`, default `0.5`) -- must be between 0 and 1 exclusive.
- `SamplingDurationSeconds` (`int`, default `30`) -- sliding window size.
- `MinimumThroughput` (`int`, default `5`) -- minimum requests before circuit evaluates.
- `BreakDurationSeconds` (`int`, default `60`) -- how long circuit stays open.

#### RetryConfig

**File:** `src/Content/Domain/Domain.Common/Config/AI/Resilience/RetryConfig.cs`

Properties:
- `MaxAttempts` (`int`, default `2`) -- includes the initial attempt.
- `BaseDelaySeconds` (`double`, default `1.0`) -- base for exponential backoff with jitter.
- `BackoffType` (`string`, default `"Exponential"`) -- "Exponential" or "Linear".

#### TimeoutConfig

**File:** `src/Content/Domain/Domain.Common/Config/AI/Resilience/TimeoutConfig.cs`

Properties:
- `PerAttemptSeconds` (`int`, default `30`) -- timeout per individual attempt.

#### DegradedModeConfig

**File:** `src/Content/Domain/Domain.Common/Config/AI/Resilience/DegradedModeConfig.cs`

Properties:
- `RetryQueueTtlSeconds` (`int`, default `300`) -- how long queued requests survive before TTL expiry.
- `MaxQueueSize` (`int`, default `100`) -- maximum items in the retry queue.

### Existing File Modifications

#### GovernanceConfig -- Add Escalation Property

**File:** `src/Content/Domain/Domain.Common/Config/AI/GovernanceConfig.cs`

Add a using for `Domain.Common.Config.AI.Governance` and a new property:

```csharp
/// <summary>
/// Gets or sets the human escalation configuration for approval workflows
/// triggered when agents exceed their authority.
/// </summary>
public EscalationConfig Escalation { get; set; } = new();
```

Note: `GovernanceConfig` currently uses `init` setters. The new `Escalation` property should use `set` (not `init`) for consistency with how config binding works and how other nested config properties are declared throughout the codebase (e.g., `RagConfig`, `AIConfig`). This is a property addition, not a change to existing properties.

#### AIConfig -- Add Resilience Property

**File:** `src/Content/Domain/Domain.Common/Config/AI/AIConfig.cs`

Add a using for `Domain.Common.Config.AI.Resilience` and a new property:

```csharp
/// <summary>
/// Gets or sets the LLM provider resilience configuration including fallback chains,
/// circuit breakers, retry policies, and degraded mode behavior.
/// </summary>
public ResilienceConfig Resilience { get; set; } = new();
```

Update the XML doc config hierarchy comment at the top of `AIConfig` to include the new `Resilience` entry:
```
/// +-- Resilience        -- LLM fallback chains, circuit breakers, retry, degraded mode
```

### FluentValidation Validators

#### EscalationConfigValidator

**File:** `src/Content/Application/Application.Core/Validation/EscalationConfigValidator.cs`
**Namespace:** `Application.Core.Validation`

Extends `AbstractValidator<EscalationConfig>`. Rules:
- `DefaultTimeoutSeconds` must be `>= 0` (zero is valid for informational-only).
- `PriorityLevels` must not be empty (when `Enabled == true`, it should be configured; use `When(x => x.Enabled, ...)` conditional).
- Each `PriorityLevels` entry: `TimeoutSeconds >= 0`.
- `DefaultTimeoutAction` must be a defined enum value (use `IsInEnum()`).
- `DefaultApprovalStrategy` must be a defined enum value.

Auto-discovered by `services.AddValidatorsFromAssembly(assembly)` in `Application.Core/DependencyInjection.cs` -- no DI registration changes needed.

#### ResilienceConfigValidator

**File:** `src/Content/Application/Application.Core/Validation/ResilienceConfigValidator.cs`
**Namespace:** `Application.Core.Validation`

Extends `AbstractValidator<ResilienceConfig>`. Rules (all conditional on `Enabled == true` unless noted):
- `FallbackChain` must not be empty.
- Each `FallbackChain` entry: `DeploymentId` must not be empty, `ClientType` must be defined enum value.
- `CircuitBreaker.FailureRatio` must be `> 0` and `< 1`.
- `CircuitBreaker.SamplingDurationSeconds` must be `> 0`.
- `CircuitBreaker.MinimumThroughput` must be `> 0`.
- `CircuitBreaker.BreakDurationSeconds` must be `> 0`.
- `Retry.MaxAttempts` must be `>= 1`.
- `Retry.BaseDelaySeconds` must be `>= 0`.
- `Timeout.PerAttemptSeconds` must be `> 0`.
- `DegradedMode.MaxQueueSize` must be `> 0`.
- `DegradedMode.RetryQueueTtlSeconds` must be `> 0`.

When `Enabled == false`, skip the `FallbackChain` non-empty check (the chain is irrelevant when resilience is off). The numeric range validations should still apply regardless of `Enabled` to catch typos early.

---

## Key Design Decisions

1. **Config classes are mutable (`set`)** -- matching the majority pattern in the codebase (`RagConfig`, `OrchestrationConfig`, `AIConfig`). The Options pattern requires settable properties for config binding.

2. **Dictionary keys for PriorityLevels use strings, not enums** -- `Dictionary<string, EscalationPriorityConfig>` keyed by the enum name string ("Informational", "Blocking", "Critical"). This is how appsettings.json config binding works with dictionaries -- it uses string keys that resolve at runtime.

3. **Validators are conditional on `Enabled`** -- the `FallbackChain` non-empty rule only fires when resilience is enabled, preventing validation errors for consumers who haven't configured fallback providers. Numeric range validations always apply.

4. **No new DI registration needed** -- validators are auto-discovered by `AddValidatorsFromAssembly` in `Application.Core/DependencyInjection.cs`. Config binding via Options pattern is handled in section-19 (DI registration) and section-20 (appsettings.json).

5. **`FallbackProviderConfig` uses `AIAgentFrameworkClientType`** -- reuses the existing enum rather than creating a new provider type enum, since the fallback chain maps directly to `IChatClientFactory.GetChatClientAsync()` which already uses this type.

---

## Implementation Notes

**Status:** Complete

### Deviations from Plan

1. **String properties instead of enum types in EscalationConfig:** `DefaultTimeoutAction` and `DefaultApprovalStrategy` are `string` instead of `EscalationTimeoutAction`/`ApprovalStrategyType` enums. Reason: Domain.Common cannot reference Domain.AI (would create circular dependency). Validation against enum values happens at the Application layer via `Enum.GetNames<T>()` with case-insensitive comparison.

2. **GovernanceConfig.Escalation uses `init` (not `set`):** Per code review — all sibling properties on GovernanceConfig use `init`, so the new property matches.

3. **Additional validation rules (per code review):**
   - BackoffType validated against ["Exponential", "Linear"] allowlist
   - Case-insensitive string comparison for all string-enum validations
   - PriorityLevels dictionary keys validated against EscalationPriority enum names
   - FailureRatio uses `Must()` instead of chained `GreaterThan/LessThan` for correct error message attachment

### Test Results
- 7 EscalationConfigValidator tests (valid, negative timeout, zero timeout, negative priority timeout, empty priority levels, invalid timeout action, invalid approval strategy)
- 12 ResilienceConfigValidator tests (valid, empty chain, negative/above-one failure ratio, negative timeout, zero queue, missing deployment ID, disabled skips chain, negative retry delay, zero throughput, invalid backoff type, disabled still validates numerics)
- All 19 tests pass
