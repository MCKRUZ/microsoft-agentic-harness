# Phase 2: Enterprise Trust — TDD Plan

Testing framework: xUnit + Moq + FluentAssertions. Naming: `MethodName_Scenario_ExpectedResult`. Arrange-Act-Assert pattern. Config mocked via `Mock.Of<IOptionsMonitor<T>>()`.

---

## Part A: Human Escalation

### A1. Domain Models — Escalation Primitives

No tests needed for pure record types. Validate factory methods and computed properties if any:

```csharp
// Test: EscalationRequest_WithDefaults_SetsExpectedValues
// Test: EscalationOutcome_Approved_IsApprovedTrue
// Test: EscalationOutcome_Denied_IsApprovedFalse
// Test: EscalationAuditRecord_RequestType_SerializesCorrectly
```

### A2. Approval Strategies

**AnyOfApprovalStrategyTests:**
```csharp
// Test: EvaluateDecision_SingleApproval_ResolvesApproved
// Test: EvaluateDecision_SingleDenial_ResolvesDenied
// Test: EvaluateDecision_NoDecisions_NotResolved
// Test: EvaluateDecision_MultipleApprovers_FirstResponseWins
```

**AllOfApprovalStrategyTests:**
```csharp
// Test: EvaluateDecision_AllApproved_ResolvesApproved
// Test: EvaluateDecision_SingleDenialAmongMultiple_ResolvesDeniedImmediately
// Test: EvaluateDecision_PartialApprovals_NotResolved
// Test: EvaluateDecision_SingleApprover_ApprovesImmediately
```

**QuorumApprovalStrategyTests:**
```csharp
// Test: EvaluateDecision_QuorumMet_ResolvesApproved (2-of-3 with 2 approvals)
// Test: EvaluateDecision_QuorumImpossible_ResolvesDenied (2-of-3 with 2 denials)
// Test: EvaluateDecision_InsufficientVotes_NotResolved
// Test: EvaluateDecision_EdgeCase_OneOfOne_ResolvesOnFirst
// Test: EvaluateDecision_EdgeCase_TwoOfThree_NeedsExactQuorum
// Test: EvaluateDecision_ThresholdEqualsTotal_BehavesLikeAllOf
```

### A3. Escalation Service

**DefaultEscalationServiceTests:**
```csharp
// Test: RequestEscalationAsync_CreatesEscalation_NotifiesApprovers
// Test: RequestEscalationAsync_BlockingMode_AwaitsOutcome
// Test: QueueEscalationAsync_ReturnsEscalationId_DoesNotBlock
// Test: SubmitDecisionAsync_TriggersStrategyEvaluation_ReturnsOutcomeIfResolved
// Test: SubmitDecisionAsync_PartialDecision_ReturnsNull
// Test: SubmitDecisionAsync_UnknownEscalationId_ReturnsNull
// Test: Timeout_FiresDenyAndEscalate_CompletesWithTimedOut
// Test: Timeout_CallerCancelled_PropagatesCancellation
// Test: ConcurrentDecisions_ThreadSafe_NoRaceConditions
// Test: GetPendingEscalationsAsync_ReturnsOnlyPending
// Test: RequestEscalationAsync_AuditsRequest
// Test: SubmitDecisionAsync_AuditsDecision
// Test: Timeout_AuditsOutcome
```

### A4. Notification Adapters

**CompositeEscalationNotifierTests:**
```csharp
// Test: NotifyEscalationRequestedAsync_FansOutToAllChannels
// Test: NotifyEscalationRequestedAsync_ChannelFailure_DoesNotBlockOthers
// Test: NotifyEscalationRequestedAsync_ChannelFailure_LogsWarning
// Test: NotifyEscalationResolvedAsync_FansOutToAllChannels
// Test: NotifyEscalationExpiringAsync_FansOutToAllChannels
// Test: NoChannelsRegistered_CompletesSuccessfully
```

### A5. Escalation Audit Store

**JsonlEscalationAuditStoreTests:**
```csharp
// Test: RecordRequestAsync_AppendsToFile
// Test: RecordDecisionAsync_AppendsToFile
// Test: RecordOutcomeAsync_AppendsToFile
// Test: GetHistoryAsync_ReturnsAllRecordsForEscalation
// Test: GetHistoryAsync_UnknownId_ReturnsEmpty
// Test: ConcurrentWrites_NoCorruption
// Test: RecordType_Discriminator_DeserializesCorrectly
```

### A6. Governance Pipeline Integration

```csharp
// Test: GovernancePolicyBehavior_RequireApproval_TriggersEscalationService
// Test: GovernancePolicyBehavior_RequireApprovalBlocking_AwaitsOutcome
// Test: GovernancePolicyBehavior_RequireApprovalApproved_ProceedsWithNext
// Test: GovernancePolicyBehavior_RequireApprovalDenied_ReturnsDeniedResult
// Test: GovernancePolicyBehavior_RequireApprovalQueueAndContinue_ReturnsPendingResult
// Test: Supervisor_AutonomyExceeded_TriggersEscalation
// Test: Supervisor_AutonomyExceeded_ApprovalGranted_RetriesDelegation
```

### A7. Escalation Configuration

**EscalationConfigValidatorTests:**
```csharp
// Test: Validate_ValidConfig_NoErrors
// Test: Validate_NegativeTimeout_HasError
// Test: Validate_ZeroTimeout_Allowed (informational)
// Test: Validate_InvalidTimeoutAction_HasError
// Test: Validate_MissingPriorityLevels_HasError
```

### A8. OTel Instrumentation

```csharp
// Test: EscalationMetrics_RequestsCounter_Increments
// Test: EscalationMetrics_ResolutionsCounter_IncrementsWithTags
// Test: EscalationMetrics_DurationHistogram_RecordsValue
// Test: EscalationConventions_Constants_FollowNamingConvention
```

---

## Part B: Fallback Chains

### B1. Domain Models — Resilience Primitives

```csharp
// Test: FallbackMetadata_NoFallback_IsFallbackFalse
// Test: FallbackMetadata_WithFallback_IsFallbackTrue
// Test: FallbackMetadata_DisabledCapabilities_ReflectsProviderDiff
// Test: ProviderExhaustedException_ContainsRetryAfterAndFailedProviders
```

### B2. Resilient Chat Client

**ResilientChatClientTests:**
```csharp
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

### B3. Per-Provider Resilience Pipeline

**ProviderResiliencePipelineTests:**
```csharp
// Test: Pipeline_TransientError_RetriesToConfiguredMax
// Test: Pipeline_Http429_TriggersRetry
// Test: Pipeline_Http500_TriggersRetry
// Test: Pipeline_FailureRatioExceeded_OpensCircuit
// Test: Pipeline_CircuitOpen_ThrowsBrokenCircuitException
// Test: Pipeline_Timeout_CancelsAttempt
// Test: Pipeline_SuccessAfterRetry_ResetsCircuit
// Test: Pipeline_ConfigValues_AppliedCorrectly
```

### B4. Provider Health Monitor

**PollyProviderHealthMonitorTests:**
```csharp
// Test: GetProviderHealth_CircuitClosed_ReturnsHealthy
// Test: GetProviderHealth_CircuitHalfOpen_ReturnsDegraded
// Test: GetProviderHealth_CircuitOpen_ReturnsUnavailable
// Test: GetAllProviderHealth_ReturnsAllProviders
// Test: IsAnyProviderHealthy_AllOpen_ReturnsFalse
// Test: IsAnyProviderHealthy_OneClosed_ReturnsTrue
// Test: OnCircuitStateChanged_Fires_OnTransition
```

### B5. Degraded Mode — Retry Queue

**LlmRetryQueueTests:**
```csharp
// Test: Enqueue_AddsToQueue_ReturnsWithinMaxSize
// Test: Enqueue_ExceedsMaxSize_RejectsOldest
// Test: Drain_ProviderRecovered_RetriesQueuedRequests
// Test: Drain_CallerCancelled_SkipsRequest
// Test: TtlExpiry_CompletesWithProviderExhaustedException
// Test: Drain_SuccessfulRetry_CompletesTcs
// Test: Drain_RetryFails_RequeuesOrExpires
```

### B6. Fallback Configuration

**ResilienceConfigValidatorTests:**
```csharp
// Test: Validate_ValidConfig_NoErrors
// Test: Validate_EmptyFallbackChain_HasError
// Test: Validate_NegativeFailureRatio_HasError
// Test: Validate_FailureRatioAboveOne_HasError
// Test: Validate_NegativeTimeout_HasError
// Test: Validate_ZeroMaxQueueSize_HasError
// Test: Validate_MissingDeploymentId_HasError
```

### B7. Provider Capability Registry

```csharp
// Test: GetCapabilities_ConfiguredProvider_ReturnsFromConfig
// Test: GetCapabilities_UnconfiguredProvider_ReturnsFullCapabilities
// Test: DiffCapabilities_PrimaryHasVision_FallbackDoesNot_ReportsDisabled
// Test: DiffCapabilities_IdenticalProviders_NothingDisabled
```

### B8. Integration with AgentExecutionContextFactory

```csharp
// Test: CreateContext_ResilienceEnabled_UsesResilientProvider
// Test: CreateContext_ResilienceDisabled_UsesOriginalFactory
// Test: CreateContext_ResilientProviderNotRegistered_FallsBackToFactory
```

### B9. OTel Instrumentation

```csharp
// Test: ResilienceMetrics_FallbackActivations_IncrementsPerSwitch
// Test: ResilienceMetrics_CircuitStateChanges_IncrementsWithTags
// Test: ResilienceMetrics_ProviderDuration_RecordsHistogram
// Test: ResilienceConventions_Constants_FollowNamingConvention
```

---

## Part C: Cross-Cutting Concerns

### C1. DI Registration

```csharp
// Test: AddEscalationServices_RegistersAllExpectedTypes
// Test: AddResilienceServices_RegistersAllExpectedTypes
// Test: AddResilienceServices_DisabledConfig_DoesNotRegisterHostedService
// Test: IApprovalStrategy_KeyedDI_ResolvesCorrectStrategy
// Test: CompositeNotifier_DoesNotContainItself (regression for infinite recursion)
```

### C2. Config Binding

```csharp
// Test: EscalationConfig_BindsFromAppsettings
// Test: ResilienceConfig_BindsFromAppsettings
// Test: FallbackProviderConfig_BindsClientTypeAndDeploymentId
```
