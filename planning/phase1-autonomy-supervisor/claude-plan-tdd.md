# TDD Plan — Phase 1: Autonomy Tiers & Supervisor Agent

Testing framework: **xUnit** + **Moq** + **FluentAssertions**
Test naming: `MethodName_Scenario_ExpectedResult`
Existing patterns: `TestableAIAgent`, `TestableAgentSession`, `CreateResolver()` helpers

---

## Section 1: Domain Models — Autonomy Tiers

No tests needed — pure enums and records with no behavior. Compilation validates correctness.

---

## Section 2: Domain Models — Supervisor & Delegation

### DelegationResult factory tests

```csharp
// Test: DelegationResult.Success creates result with IsSuccess=true, populated output/tokens/duration
// Test: DelegationResult.Fail creates result with IsSuccess=false, populated FailureReason
// Test: DelegationResult.FailAutonomyExceeded creates result with populated AutonomyExceeded
```

---

## Section 3: Application Interfaces

No tests — interfaces only. Tested via implementation sections.

---

## Section 4: Autonomy Tier Rule Provider

### AutonomyTierRuleProvider tests (`Infrastructure.AI.Tests/Governance/AutonomyTierRuleProviderTests.cs`)

```csharp
// Test: GetRulesAsync_RestrictedTier_GeneratesGlobalAskRule
//   Arrange: Mock IAutonomyTierResolver returning Restricted, config with Restricted tier policy
//   Assert: Returns Ask rule with pattern "*", Priority 0, Source AutonomyTier

// Test: GetRulesAsync_SupervisedTier_GeneratesGlobalAskRule
//   Assert: Same as Restricted — both generate Ask baseline

// Test: GetRulesAsync_AutonomousTier_GeneratesGlobalAllowRule
//   Assert: Returns Allow rule with pattern "*", Priority 0

// Test: GetRulesAsync_WithToolOverrides_GeneratesOverrideRulesAtHigherPriority
//   Arrange: Config with Restricted tier + ToolOverrides { "query_kg": "Allow" }
//   Assert: Returns global Ask at Priority 0 AND specific Allow for "query_kg" at Priority 10

// Test: GetRulesAsync_NoTierPolicy_UsesDefaultBehavior
//   Arrange: Config has no TierPolicies entry for the agent's tier
//   Assert: Falls back to PermissionsConfig.DefaultBehavior
```

### DefaultAutonomyTierResolver tests (`Infrastructure.AI.Tests/Governance/DefaultAutonomyTierResolverTests.cs`)

```csharp
// Test: Resolve_KnownSubagentType_ReturnsDefinitionAutonomyLevel
//   Arrange: Registry returns SubagentDefinition with AutonomyLevel = Restricted
//   Assert: Returns Restricted

// Test: Resolve_SubagentDefinition_ReturnsDirectLevel
//   Arrange: SubagentDefinition with AutonomyLevel = Autonomous
//   Assert: Returns Autonomous

// Test: Resolve_UnknownType_ReturnsFallbackFromConfig
//   Arrange: Registry returns null for type, config DefaultAutonomyLevel = Supervised
//   Assert: Returns Supervised
```

### Permission integration tests (`Infrastructure.AI.Tests/Permissions/ThreePhasePermissionResolverAutonomyTests.cs`)

```csharp
// Test: Restricted agent with no overrides gets Ask for any tool
// Test: Restricted agent with Allow override gets Allow for that specific tool
// Test: Autonomous agent gets Allow for any tool (no manifest rules)
// Test: Autonomous agent with manifest Deny rule gets Deny (manifest overrides tier)
// Test: Supervised agent with session Allow override gets Allow for that tool
```

---

## Section 5: Capability Match Strategy

### CapabilityMatchStrategy tests (`Infrastructure.AI.Tests/Agents/CapabilityMatchStrategyTests.cs`)

```csharp
// === Filtering ===
// Test: SelectAgent_AgentBelowMinimumTier_FilteredOut
//   Arrange: Agent at Restricted, minimum tier Supervised
//   Assert: Agent not in candidates

// Test: SelectAgent_AgentLacksAllRequiredTools_FilteredOut
//   Arrange: Agent has [tool_a], required [tool_a, tool_b]
//   Assert: Agent not in candidates (partial coverage OK, zero overlap filtered)

// Test: SelectAgent_NoCandidatesAfterFiltering_ReturnsNull

// === Scoring ===
// Test: SelectAgent_ToolCoverage_ScoresCorrectly
//   Arrange: Required [a, b, c], Agent has [a, b]
//   Assert: ToolCoverage = 2/3

// Test: SelectAgent_TypeAlignment_ExactMatch_ScoresOne
//   Arrange: Task keywords map to Explore, agent type is Explore
//   Assert: TypeAlignment = 1.0

// Test: SelectAgent_TypeAlignment_General_ScoresHalf
//   Arrange: Agent type is General
//   Assert: TypeAlignment = 0.5

// Test: SelectAgent_TierHeadroom_HigherTierScoresMore
//   Arrange: MinTier=Restricted, Agent1=Supervised, Agent2=Autonomous
//   Assert: Agent2 has higher TierHeadroom

// === Selection ===
// Test: SelectAgent_TiedScore_PrefersLowerTier
//   Arrange: Two agents with identical scores but different tiers
//   Assert: Lower tier agent selected (least privilege)

// Test: SelectAgent_SingleCandidate_SkipsScoring
//   Assert: Returns the single candidate directly

// Test: SelectAgent_WeightsNormalized_SumNotOne
//   Arrange: Weights configured as 0.4, 0.3, 0.5 (sum 1.2)
//   Assert: Scores still fall within 0.0-1.0 range

// === Keyword Classifier ===
// Test: ClassifyTask_SearchKeywords_MapsToExplore
// Test: ClassifyTask_CreateKeywords_MapsToExecute
// Test: ClassifyTask_MixedKeywords_MostMatchesWins
// Test: ClassifyTask_TiedKeywords_PrefersExecute
// Test: ClassifyTask_NoKeywords_MapsToGeneral
```

---

## Section 6: Supervisor Implementation

### CapabilityMatchSupervisor tests (`Infrastructure.AI.Tests/Agents/CapabilityMatchSupervisorTests.cs`)

```csharp
// === Happy path ===
// Test: DelegateAsync_SuccessfulDelegation_ReturnsDelegationResultSuccess
//   Arrange: Mock strategy returns agent, mock execution returns success
//   Assert: Result.IsSuccess, output populated

// Test: DelegateAsync_RecordsPendingThenCompletedToStore
//   Assert: IDelegationStore.AppendAsync called twice (Pending, then Completed)

// Test: DelegateAsync_EmitsAuditEvents
//   Assert: IGovernanceAuditService.Log called for decision and outcome

// === Failure paths ===
// Test: DelegateAsync_DepthExceeded_ReturnsFailWithDepthExceededReason
//   Arrange: CurrentDelegationDepth >= MaxDelegationDepth
//   Assert: Result.IsSuccess = false, reason contains "depth"

// Test: DelegateAsync_NoCapableAgent_ReturnsFailWithNoAgentReason
//   Arrange: Strategy returns null
//   Assert: Result.IsSuccess = false

// Test: DelegateAsync_AgentFailsWithAutonomyExceeded_PropagatesResult
//   Arrange: Agent execution returns AutonomyExceeded
//   Assert: Result.AutonomyExceeded populated

// Test: DelegateAsync_AgentTimesOut_ReturnsFail
//   Arrange: DelegationTimeoutSeconds = 0 (immediate timeout)
//   Assert: Result.IsSuccess = false

// === Tool overrides ===
// Test: DelegateAsync_WithToolOverrides_OverridesAppliedToAgentContext
//   Assert: AgentExecutionContext tools include overrides

// === Concurrency ===
// Test: DelegateAsync_MaxConcurrentReached_BlocksUntilSlotFree

// === Cancellation ===
// Test: CancelDelegationAsync_ActiveDelegation_PropagatesCancellation
//   Assert: CancellationToken is triggered, delegation state becomes Cancelled

// Test: CancelDelegationAsync_UnknownDelegationId_ReturnsFalse

// === Multi-level ===
// Test: DelegateAsync_SetsDepthPlusOneInChildContext
//   Assert: Child AgentExecutionContext.DelegationDepth = parent + 1

// Test: DelegateAsync_SetsParentDelegationIdInChildRecord
//   Assert: Child DelegationRecord.ParentDelegationId = parent's DelegationId
```

---

## Section 7: JSONL Delegation Store

### JsonlDelegationStore tests (`Infrastructure.AI.Tests/Agents/JsonlDelegationStoreTests.cs`)

```csharp
// === Round-trip ===
// Test: AppendAsync_ThenGetByIdAsync_ReturnsRecord
// Test: AppendAsync_MultipleTimes_GetByIdAsync_ReturnsLatestState
//   Arrange: Append Pending, then Completed for same DelegationId
//   Assert: GetByIdAsync returns the Completed record

// === Queries ===
// Test: GetBySessionAsync_ReturnsAllRecordsDeduplicatedById
//   Arrange: Append 3 delegations (2 state changes each = 6 lines)
//   Assert: Returns 3 records (latest state for each)

// Test: GetByParentAsync_ReturnsOnlyChildDelegations
//   Arrange: Parent delegation + 2 child delegations
//   Assert: Returns 2 children

// === Filesystem ===
// Test: AppendAsync_CreatesDirectoryStructureLazily
// Test: AppendAsync_FilePathContainsSupervisorIdAndTimestamp

// === Concurrency ===
// Test: AppendAsync_ConcurrentWrites_NoCorruption
//   Arrange: 10 parallel AppendAsync calls
//   Assert: All records readable, no corrupted lines

// === Error handling ===
// Test: GetByIdAsync_PartialJsonLine_SkipsWithoutCrash
//   Arrange: Write a partial JSON line to file
//   Assert: Returns valid records, skips corrupted line
```

---

## Section 8: DI Registration & Configuration

### DI integration tests (`Infrastructure.AI.Tests/DependencyInjectionTests.cs`)

```csharp
// Test: ServiceProvider_ResolvesIAutonomyTierResolver
// Test: ServiceProvider_ResolvesISupervisor
// Test: ServiceProvider_ResolvesIDelegationStore
// Test: ServiceProvider_ResolvesISupervisorStrategy_ByKey_CapabilityMatch
// Test: ServiceProvider_ResolvesIPermissionRuleProvider_IncludesAutonomyTierProvider
//   Assert: IEnumerable<IPermissionRuleProvider> includes AutonomyTierRuleProvider

// Test: CapabilityMatchWeightsConfig_NormalizesOnConstruction
//   Arrange: Weights sum to 1.5
//   Assert: After normalization, sum to 1.0
```

---

## Section 9: Integration Tests

### End-to-end tests (`Infrastructure.AI.Tests/Integration/`)

```csharp
// Test: RestrictedAgent_ToolCall_ReturnsPermissionRequired
//   Full pipeline: create agent with Restricted tier, attempt tool call through MediatR
//   Assert: ToolPermissionBehavior returns PermissionRequired

// Test: AutonomousAgent_ToolCall_Proceeds
//   Assert: ToolPermissionBehavior allows through

// Test: Supervisor_DelegatesToAgent_AgentHitsAutonomyCeiling_SupervisorGetsStructuredFailure
//   Full pipeline: supervisor delegates to Restricted agent, agent tries write tool
//   Assert: DelegationResult.AutonomyExceeded is populated

// Test: Supervisor_MultiLevelDelegation_DepthLimitEnforced
//   Arrange: MaxDelegationDepth=2, supervisor → agent → sub-agent → sub-sub-agent
//   Assert: Third-level delegation fails with depth exceeded
```
