# Section 21: Full Test Suite

## Overview

This section covers the complete test suite for Phase 2 (Enterprise Trust). It is the final implementation step and depends on all other sections (1-20) being complete. Tests are split across two test projects:

- **`Application.Core.Tests`** -- approval strategies, config validators
- **`Infrastructure.AI.Tests`** -- escalation service, audit store, notification adapters, resilient chat client, Polly pipeline, health monitor, retry queue, DI registration, config binding

All tests use xUnit + Moq + FluentAssertions. Naming convention: `MethodName_Scenario_ExpectedResult`. Arrange-Act-Assert pattern throughout.

---

## Test Project Structure

```
src/Content/Tests/Application.Core.Tests/
  Escalation/Strategies/
    AnyOfApprovalStrategyTests.cs
    AllOfApprovalStrategyTests.cs
    QuorumApprovalStrategyTests.cs
  Validation/
    EscalationConfigValidatorTests.cs
    ResilienceConfigValidatorTests.cs

src/Content/Tests/Infrastructure.AI.Tests/
  Escalation/
    DefaultEscalationServiceTests.cs
    JsonlEscalationAuditStoreTests.cs
    CompositeEscalationNotifierTests.cs
  Resilience/
    ResilientChatClientTests.cs
    ProviderResiliencePipelineTests.cs
    PollyProviderHealthMonitorTests.cs
    LlmRetryQueueTests.cs
    ProviderCapabilityRegistryTests.cs
  DependencyInjectionTests.cs  (extend existing file)
  Config/
    ConfigBindingTests.cs
```

No new NuGet dependencies needed -- both test projects already reference `xunit`, `FluentAssertions`, `Moq`, `Microsoft.NET.Test.Sdk`, and `coverlet.collector`.

---

## Part 1: Application.Core.Tests -- Approval Strategies

### File: `src/Content/Tests/Application.Core.Tests/Escalation/Strategies/AnyOfApprovalStrategyTests.cs`

```csharp
namespace Application.Core.Tests.Escalation.Strategies;

/// <summary>
/// AnyOf strategy: first approver response wins. One approval -> approved.
/// One denial -> denied. No decisions -> not resolved.
/// </summary>
public sealed class AnyOfApprovalStrategyTests
{
    // EvaluateDecision_SingleApproval_ResolvesApproved
    // EvaluateDecision_SingleDenial_ResolvesDenied
    // EvaluateDecision_NoDecisions_NotResolved
    // EvaluateDecision_MultipleApprovers_FirstResponseWins
}
```

### File: `src/Content/Tests/Application.Core.Tests/Escalation/Strategies/AllOfApprovalStrategyTests.cs`

```csharp
namespace Application.Core.Tests.Escalation.Strategies;

/// <summary>
/// AllOf strategy: all approvers must approve. Single denial -> immediate deny.
/// Partial approvals -> not resolved. All approved -> resolved approved.
/// </summary>
public sealed class AllOfApprovalStrategyTests
{
    // EvaluateDecision_AllApproved_ResolvesApproved
    // EvaluateDecision_SingleDenialAmongMultiple_ResolvesDeniedImmediately
    // EvaluateDecision_PartialApprovals_NotResolved
    // EvaluateDecision_SingleApprover_ApprovesImmediately
}
```

### File: `src/Content/Tests/Application.Core.Tests/Escalation/Strategies/QuorumApprovalStrategyTests.cs`

```csharp
namespace Application.Core.Tests.Escalation.Strategies;

/// <summary>
/// Quorum strategy: N-of-M voting. Quorum met -> approved. Quorum impossible -> denied.
/// Insufficient votes -> not resolved.
/// </summary>
public sealed class QuorumApprovalStrategyTests
{
    // EvaluateDecision_QuorumMet_ResolvesApproved
    // EvaluateDecision_QuorumImpossible_ResolvesDenied
    // EvaluateDecision_InsufficientVotes_NotResolved
    // EvaluateDecision_EdgeCase_OneOfOne_ResolvesOnFirst
    // EvaluateDecision_EdgeCase_TwoOfThree_NeedsExactQuorum
    // EvaluateDecision_ThresholdEqualsTotal_BehavesLikeAllOf
}
```

**Helper pattern for all strategy tests:**

```csharp
private static EscalationRequest CreateRequest(
    int approverCount = 3,
    ApprovalStrategyType strategy = ApprovalStrategyType.Quorum,
    int quorumThreshold = 2)
{
    return new EscalationRequest
    {
        EscalationId = Guid.NewGuid(),
        AgentId = "test-agent",
        ToolName = "test-tool",
        Arguments = new Dictionary<string, object?>(),
        Description = "test escalation",
        RiskLevel = "high",
        Priority = EscalationPriority.Blocking,
        ApprovalStrategy = strategy,
        Approvers = Enumerable.Range(1, approverCount)
            .Select(i => $"approver-{i}").ToList(),
        QuorumThreshold = quorumThreshold,
        TimeoutSeconds = 300,
        TimeoutAction = EscalationTimeoutAction.Deny,
        RequestedAt = DateTimeOffset.UtcNow
    };
}

private static ApproverDecision Approve(string approver) =>
    new() { ApproverName = approver, Approved = true, RespondedAt = DateTimeOffset.UtcNow };

private static ApproverDecision Deny(string approver) =>
    new() { ApproverName = approver, Approved = false, RespondedAt = DateTimeOffset.UtcNow };
```

---

## Part 2: Application.Core.Tests -- Config Validators

### File: `src/Content/Tests/Application.Core.Tests/Validation/EscalationConfigValidatorTests.cs`

```csharp
namespace Application.Core.Tests.Validation;

/// <summary>
/// Validates EscalationConfig: non-negative timeouts, valid enum values,
/// priority levels must exist.
/// </summary>
public sealed class EscalationConfigValidatorTests
{
    // Validate_ValidConfig_NoErrors
    // Validate_NegativeTimeout_HasError
    // Validate_ZeroTimeout_Allowed
    // Validate_InvalidTimeoutAction_HasError
    // Validate_MissingPriorityLevels_HasError
}
```

### File: `src/Content/Tests/Application.Core.Tests/Validation/ResilienceConfigValidatorTests.cs`

```csharp
namespace Application.Core.Tests.Validation;

/// <summary>
/// Validates ResilienceConfig: non-empty fallback chain, valid ratio ranges,
/// positive timeouts, valid queue config.
/// </summary>
public sealed class ResilienceConfigValidatorTests
{
    // Validate_ValidConfig_NoErrors
    // Validate_EmptyFallbackChain_HasError
    // Validate_NegativeFailureRatio_HasError
    // Validate_FailureRatioAboveOne_HasError
    // Validate_NegativeTimeout_HasError
    // Validate_ZeroMaxQueueSize_HasError
    // Validate_MissingDeploymentId_HasError
}
```

---

## Part 3: Infrastructure.AI.Tests -- Escalation

### File: `src/Content/Tests/Infrastructure.AI.Tests/Escalation/DefaultEscalationServiceTests.cs`

```csharp
namespace Infrastructure.AI.Tests.Escalation;

/// <summary>
/// Tests DefaultEscalationService: escalation lifecycle (create, decide, timeout),
/// cancellation propagation, concurrent decisions, audit logging.
/// </summary>
public sealed class DefaultEscalationServiceTests : IDisposable
{
    // RequestEscalationAsync_CreatesEscalation_NotifiesApprovers
    // RequestEscalationAsync_BlockingMode_AwaitsOutcome
    // QueueEscalationAsync_ReturnsEscalationId_DoesNotBlock
    // SubmitDecisionAsync_TriggersStrategyEvaluation_ReturnsOutcomeIfResolved
    // SubmitDecisionAsync_PartialDecision_ReturnsNull
    // SubmitDecisionAsync_UnknownEscalationId_ReturnsNull
    // Timeout_FiresDenyAndEscalate_CompletesWithTimedOut
    // Timeout_CallerCancelled_PropagatesCancellation
    // ConcurrentDecisions_ThreadSafe_NoRaceConditions
    // GetPendingEscalationsAsync_ReturnsOnlyPending
    // RequestEscalationAsync_AuditsRequest
    // SubmitDecisionAsync_AuditsDecision
    // Timeout_AuditsOutcome
}
```

### File: `src/Content/Tests/Infrastructure.AI.Tests/Escalation/JsonlEscalationAuditStoreTests.cs`

```csharp
namespace Infrastructure.AI.Tests.Escalation;

/// <summary>
/// Tests JSONL audit store: append-only writes, round-trip serialization,
/// record type discrimination, concurrent write safety.
/// </summary>
public sealed class JsonlEscalationAuditStoreTests : IDisposable
{
    // RecordRequestAsync_AppendsToFile
    // RecordDecisionAsync_AppendsToFile
    // RecordOutcomeAsync_AppendsToFile
    // GetHistoryAsync_ReturnsAllRecordsForEscalation
    // GetHistoryAsync_UnknownId_ReturnsEmpty
    // ConcurrentWrites_NoCorruption
    // RecordType_Discriminator_DeserializesCorrectly
}
```

### File: `src/Content/Tests/Infrastructure.AI.Tests/Escalation/CompositeEscalationNotifierTests.cs`

```csharp
namespace Infrastructure.AI.Tests.Escalation;

/// <summary>
/// Tests CompositeEscalationNotifier: fan-out to all channels,
/// individual channel failure isolation, logging on failure.
/// </summary>
public sealed class CompositeEscalationNotifierTests
{
    // NotifyEscalationRequestedAsync_FansOutToAllChannels
    // NotifyEscalationRequestedAsync_ChannelFailure_DoesNotBlockOthers
    // NotifyEscalationRequestedAsync_ChannelFailure_LogsWarning
    // NotifyEscalationResolvedAsync_FansOutToAllChannels
    // NotifyEscalationExpiringAsync_FansOutToAllChannels
    // NoChannelsRegistered_CompletesSuccessfully
}
```

---

## Part 4: Infrastructure.AI.Tests -- Resilience

### File: `src/Content/Tests/Infrastructure.AI.Tests/Resilience/ResilientChatClientTests.cs`

```csharp
namespace Infrastructure.AI.Tests.Resilience;

/// <summary>
/// Tests ResilientChatClient: provider fallback iteration, metadata population,
/// streaming fallback, disposal.
/// </summary>
public sealed class ResilientChatClientTests : IDisposable
{
    // GetResponseAsync_PrimarySucceeds_NoFallback_MetadataShowsPrimary
    // GetResponseAsync_PrimaryFails_SecondarySucceeds_MetadataShowsFallback
    // GetResponseAsync_AllProvidersFail_ThrowsProviderExhaustedException
    // GetResponseAsync_CircuitOpen_SkipsProvider_TriesNext
    // GetResponseAsync_FallbackMetadata_PopulatedCorrectly
    // GetResponseAsync_FallbackMetadata_DisabledCapabilities_Populated
    // GetStreamingResponseAsync_PrimaryFails_SecondarySucceeds
    // GetStreamingResponseAsync_MidStreamFailure_RetriesFromScratch
    // GetStreamingResponseAsync_AllFail_ThrowsProviderExhaustedException
    // Dispose_DisposesAllProviderClients
}
```

### File: `src/Content/Tests/Infrastructure.AI.Tests/Resilience/ProviderResiliencePipelineTests.cs`

```csharp
namespace Infrastructure.AI.Tests.Resilience;

/// <summary>
/// Tests ProviderResiliencePipelineBuilder: retry on transient errors,
/// circuit breaker opens on failure ratio, timeout cancels.
/// Uses real Polly pipelines (not mocked) against simulated callbacks.
/// </summary>
public sealed class ProviderResiliencePipelineTests
{
    // Pipeline_TransientError_RetriesToConfiguredMax
    // Pipeline_Http429_TriggersRetry
    // Pipeline_Http500_TriggersRetry
    // Pipeline_FailureRatioExceeded_OpensCircuit
    // Pipeline_CircuitOpen_ThrowsBrokenCircuitException
    // Pipeline_Timeout_CancelsAttempt
    // Pipeline_SuccessAfterRetry_ResetsCircuit
    // Pipeline_ConfigValues_AppliedCorrectly
}
```

### File: `src/Content/Tests/Infrastructure.AI.Tests/Resilience/PollyProviderHealthMonitorTests.cs`

```csharp
namespace Infrastructure.AI.Tests.Resilience;

/// <summary>
/// Tests PollyProviderHealthMonitor: circuit state mapping to ProviderHealthState,
/// aggregate health queries, state change events.
/// </summary>
public sealed class PollyProviderHealthMonitorTests
{
    // GetProviderHealth_CircuitClosed_ReturnsHealthy
    // GetProviderHealth_CircuitHalfOpen_ReturnsDegraded
    // GetProviderHealth_CircuitOpen_ReturnsUnavailable
    // GetProviderHealth_CircuitIsolated_ReturnsUnavailable
    // GetProviderHealth_UnknownProvider_ReturnsHealthy
    // GetAllProviderHealth_ReturnsAllProviders
    // IsAnyProviderHealthy_AllOpen_ReturnsFalse
    // IsAnyProviderHealthy_OneClosed_ReturnsTrue
    // IsAnyProviderHealthy_NoProviders_ReturnsTrue
    // OnCircuitStateChanged_Fires_OnTransition
}
```

### File: `src/Content/Tests/Infrastructure.AI.Tests/Resilience/LlmRetryQueueTests.cs`

```csharp
namespace Infrastructure.AI.Tests.Resilience;

/// <summary>
/// Tests LlmRetryQueue: enqueue/drain lifecycle, TTL expiry,
/// max size enforcement, cancelled caller skip.
/// </summary>
public sealed class LlmRetryQueueTests : IAsyncLifetime
{
    // EnqueueAsync_AddsToQueue_ReturnsTaskCompletionSource
    // EnqueueAsync_ExceedsMaxSize_RejectsOldest
    // DrainAsync_ProviderRecovered_RetriesQueuedRequests
    // DrainAsync_CallerCancelled_SkipsRequest
    // TtlExpiry_CompletesWithProviderExhaustedException
    // DrainAsync_SuccessfulRetry_CompletesTcs
    // DrainAsync_RetryFails_RequeuesOrExpires
    // DrainAsync_NoHealthyProvider_DoesNotAttemptRetry
    // EnqueueAsync_QueueSize_MetricUpdated
}
```

### File: `src/Content/Tests/Infrastructure.AI.Tests/Resilience/ProviderCapabilityRegistryTests.cs`

```csharp
namespace Infrastructure.AI.Tests.Resilience;

/// <summary>
/// Tests ProviderCapabilityRegistry: config-driven capability lookup,
/// default capabilities for unconfigured providers, capability diffing.
/// </summary>
public sealed class ProviderCapabilityRegistryTests
{
    // GetCapabilities_ConfiguredProvider_ReturnsFromConfig
    // GetCapabilities_UnconfiguredProvider_ReturnsFullCapabilities
    // DiffCapabilities_PrimaryHasVision_FallbackDoesNot_ReportsDisabled
    // DiffCapabilities_IdenticalProviders_NothingDisabled
}
```

---

## Part 5: Infrastructure.AI.Tests -- OTel Instrumentation

```csharp
// EscalationMetrics_RequestsCounter_Increments
// EscalationMetrics_ResolutionsCounter_IncrementsWithTags
// EscalationMetrics_DurationHistogram_RecordsValue
// EscalationConventions_Constants_FollowNamingConvention
// ResilienceMetrics_FallbackActivations_IncrementsPerSwitch
// ResilienceMetrics_CircuitStateChanges_IncrementsWithTags
// ResilienceMetrics_ProviderDuration_RecordsHistogram
// ResilienceConventions_Constants_FollowNamingConvention
```

**Pattern for convention tests:** Use reflection to verify all `public const string` fields follow the expected prefix:

```csharp
var fields = typeof(EscalationConventions)
    .GetFields(BindingFlags.Public | BindingFlags.Static)
    .Where(f => f.IsLiteral && f.FieldType == typeof(string));

foreach (var field in fields)
{
    var value = (string)field.GetValue(null)!;
    value.Should().StartWith("agent.escalation.",
        because: $"convention {field.Name} must follow naming pattern");
}
```

---

## Part 6: Cross-Cutting -- DI Registration Tests

### File: `src/Content/Tests/Infrastructure.AI.Tests/DependencyInjectionTests.cs` (extend existing)

```csharp
// AddEscalationServices_RegistersAllExpectedTypes
// AddResilienceServices_RegistersAllExpectedTypes
// AddResilienceServices_DisabledConfig_DoesNotRegisterHostedService
// IApprovalStrategy_KeyedDI_ResolvesCorrectStrategy
// CompositeNotifier_DoesNotContainItself
```

---

## Part 7: Cross-Cutting -- Config Binding Tests

### File: `src/Content/Tests/Infrastructure.AI.Tests/Config/ConfigBindingTests.cs`

```csharp
namespace Infrastructure.AI.Tests.Config;

/// <summary>
/// Tests config binding from appsettings JSON structure to
/// EscalationConfig and ResilienceConfig POCOs.
/// </summary>
public sealed class ConfigBindingTests
{
    // EscalationConfig_BindsFromAppsettings
    // ResilienceConfig_BindsFromAppsettings
    // FallbackProviderConfig_BindsClientTypeAndDeploymentId
}
```

---

## Part 8: Governance Pipeline Integration Tests

```csharp
// GovernancePolicyBehavior_RequireApproval_TriggersEscalationService
// GovernancePolicyBehavior_RequireApprovalBlocking_AwaitsOutcome
// GovernancePolicyBehavior_RequireApprovalApproved_ProceedsWithNext
// GovernancePolicyBehavior_RequireApprovalDenied_ReturnsDeniedResult
// GovernancePolicyBehavior_RequireApprovalQueueAndContinue_ReturnsPendingResult
// Supervisor_AutonomyExceeded_TriggersEscalation
// Supervisor_AutonomyExceeded_ApprovalGranted_RetriesDelegation
```

---

## Dependencies

This section depends on **all prior sections (1-20)** being implemented. Specifically:

- **Sections 1-2** (domain models): Required for all test types
- **Section 3** (OTel conventions): Required for convention constant reflection tests
- **Section 4** (config + validators): Required for validator tests and config binding tests
- **Section 5** (approval strategies): Required for strategy tests
- **Sections 6-7** (interfaces): Required for mock contracts
- **Sections 8-16** (implementations): Required for classes under test
- **Section 17** (governance integration): Required for governance pipeline tests
- **Section 19** (DI registration): Required for DI resolution tests
- **Section 20** (appsettings): Required for config binding tests

---

## Build and Run

```powershell
# Build the solution
dotnet build src/AgenticHarness.slnx

# Run all tests
dotnet test src/AgenticHarness.slnx

# Run only Phase 2 tests (by namespace filter)
dotnet test src/AgenticHarness.slnx --filter "FullyQualifiedName~Escalation|FullyQualifiedName~Resilience|FullyQualifiedName~ConfigBinding"

# With coverage
dotnet test src/AgenticHarness.slnx --collect:"XPlat Code Coverage"
```

Target: 80% coverage minimum on all new Phase 2 code.

---

## Implementation Notes (Post-Implementation)

### Deviations from Plan

1. **No new test files created** — All test files listed in this section were already implemented as part of the TDD workflow in sections 1-20. Each section's TDD cycle produced the corresponding test file alongside the implementation. This section served as a verification pass confirming complete coverage.

2. **Config binding tests placed in Presentation.Common.Tests** — The plan specified `Infrastructure.AI.Tests/Config/ConfigBindingTests.cs` but config binding tests were placed in `Presentation.Common.Tests/Extensions/IServiceCollectionExtensionsTests.cs` (sections 19-20), which is the natural home since config binding is a Presentation-layer concern.

### Test Inventory

| Test Project | Phase 2 Tests | Status |
|-------------|---------------|--------|
| Domain.AI.Tests | 18 (EscalationDomainModelTests, ResilienceDomainModelTests, TelemetryConventionsTests) | All passing |
| Application.Core.Tests | 42 (ApprovalStrategy tests, ConfigValidator tests, DI keyed tests) | All passing |
| Application.AI.Common.Tests | 22 (GovernancePolicyBehaviorEscalationTests, MetricsInstrumentTests) | All passing |
| Infrastructure.AI.Tests | 84 (DefaultEscalationService, AuditStore, CompositeNotifier, ResilientChatClient, Polly pipeline, HealthMonitor, RetryQueue, CapabilityRegistry, DI tests) | All passing |
| Presentation.Common.Tests | 5 (EscalationConfig binding, ResilienceConfig binding, FallbackProvider binding) | All passing |
| Presentation.AgentHub.Tests | 14 (AgUiEscalationNotifier, AgUiEscalationEventSerialization) | All passing |
| **Total Phase 2** | **185** | **All passing** |

### Pre-existing Failures (Not Phase 2)

- 9 AgentFactoryTests in Application.AI.Common.Tests — pre-existing, unrelated to Phase 2
- 9 MetricsE2E tests in Presentation.AgentHub.Tests — require Docker (Testcontainers), expected to fail without Docker running
