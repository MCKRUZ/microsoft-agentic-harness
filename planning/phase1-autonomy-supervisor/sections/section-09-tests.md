# Section 9: Tests

## Overview

This section covers all unit tests, integration tests, and pipeline tests for the Phase 1 Autonomy Tiers and Supervisor Agent feature. Tests are organized by component in the existing `Infrastructure.AI.Tests` project, following established conventions (xUnit + Moq + FluentAssertions, `MethodName_Scenario_ExpectedResult` naming).

All tests in this section validate implementations from sections 1 through 8. Each test file is self-contained with its own helper methods and mock setups, following the patterns established in existing test files like `ThreePhasePermissionResolverTests.cs`, `JsonlAgentHistoryStoreTests.cs`, and `BuiltInSubagentProfilesTests.cs`.

**Dependencies:** Sections 1-8 must be implemented before these tests compile and pass. Tests can be written in parallel with implementation (TDD), but final green-bar requires all prior sections.

---

## Test Infrastructure

**Project:** `src/Content/Tests/Infrastructure.AI.Tests/Infrastructure.AI.Tests.csproj`

No new project references needed -- the existing test project already references `Infrastructure.AI`, `Application.AI.Common`, and `Application.Core`.

**Packages used (all already in the project):**
- `xunit` -- test framework
- `Moq` -- mocking
- `FluentAssertions` -- assertion library
- `Microsoft.NET.Test.Sdk` -- test runner

**Run command:** `dotnet test src/AgenticHarness.slnx`

---

## Unit Test Files

### 1. DelegationResult Factory Tests

**File:** `src/Content/Tests/Infrastructure.AI.Tests/Agents/DelegationResultTests.cs`

**Namespace:** `Infrastructure.AI.Tests.Agents`

Tests the static factory methods on `DelegationResult` (from Section 2, `Domain.AI/Orchestration/DelegationResult.cs`). These are pure record construction tests -- no mocks required.

```csharp
// Test: Success_CreatesResultWithIsSuccessTrue_AndPopulatedFields
//   Arrange: call DelegationResult.Success("output text", tokens: 150, durationMs: 2500)
//   Assert: IsSuccess == true, Output == "output text", TokensUsed == 150, DurationMs == 2500,
//           FailureReason is null, AutonomyExceeded is null

// Test: Fail_CreatesResultWithIsSuccessFalse_AndPopulatedFailureReason
//   Arrange: call DelegationResult.Fail("agent timed out")
//   Assert: IsSuccess == false, FailureReason == "agent timed out", Output is null

// Test: FailAutonomyExceeded_CreatesResultWithPopulatedAutonomyExceeded
//   Arrange: create AutonomyExceededResult { AttemptedAction = "bash", CurrentLevel = Restricted, RequiredLevel = Autonomous, Reason = "tier violation" }
//   Arrange: call DelegationResult.FailAutonomyExceeded(exceededResult)
//   Assert: IsSuccess == false, AutonomyExceeded is not null, AutonomyExceeded.AttemptedAction == "bash"
```

---

### 2. DefaultAutonomyTierResolver Tests

**File:** `src/Content/Tests/Infrastructure.AI.Tests/Governance/DefaultAutonomyTierResolverTests.cs`

**Namespace:** `Infrastructure.AI.Tests.Governance`

Tests `DefaultAutonomyTierResolver` (from Section 4, implemented in Infrastructure.AI). Requires mocks of `ISubagentProfileRegistry` and `IOptionsMonitor<AppConfig>`.

**Key types referenced:**
- `Domain.AI.Governance.AutonomyLevel` (Section 1)
- `Domain.AI.Agents.SubagentDefinition` (Section 1 modification -- adds `AutonomyLevel` property)
- `Domain.AI.Agents.SubagentType` (existing)
- `Application.AI.Common.Interfaces.Agents.ISubagentProfileRegistry` (existing)
- `Domain.Common.Config.AI.Permissions.PermissionsConfig` (Section 8 adds `DefaultAutonomyLevel`)

```csharp
// Test: Resolve_KnownSubagentType_ReturnsDefinitionAutonomyLevel
//   Arrange: Mock ISubagentProfileRegistry.GetProfile(SubagentType.Explore) returns
//            SubagentDefinition with AutonomyLevel = Restricted
//   Act: resolver.Resolve(SubagentType.Explore)
//   Assert: returns AutonomyLevel.Restricted

// Test: Resolve_SubagentDefinition_ReturnsDirectLevel
//   Arrange: SubagentDefinition { AgentType = General, AutonomyLevel = Autonomous }
//   Act: resolver.Resolve(definition)
//   Assert: returns AutonomyLevel.Autonomous

// Test: Resolve_UnknownType_ReturnsFallbackFromConfig
//   Arrange: Mock registry returns SubagentDefinition with default AutonomyLevel (Supervised),
//            config DefaultAutonomyLevel = "Supervised"
//   Act: resolver.Resolve(SubagentType.General)
//   Assert: returns AutonomyLevel.Supervised
```

The resolver has two overloads: one takes `SubagentType` (looks up via registry), the other takes `SubagentDefinition` directly. Both read the `AutonomyLevel` property added in Section 1. The fallback path uses `PermissionsConfig.DefaultAutonomyLevel` from Section 8.

---

### 3. AutonomyTierRuleProvider Tests

**File:** `src/Content/Tests/Infrastructure.AI.Tests/Governance/AutonomyTierRuleProviderTests.cs`

**Namespace:** `Infrastructure.AI.Tests.Governance`

Tests `AutonomyTierRuleProvider` (from Section 4, `Application.Core/Permissions/AutonomyTierRuleProvider.cs`). This is the key integration point between autonomy tiers and the 3-phase permission system.

**Mocks needed:**
- `IAutonomyTierResolver` -- returns the tier for a given agent
- `IOptionsMonitor<AppConfig>` -- provides `PermissionsConfig.TierPolicies` configuration

**Key assertion patterns:** Each test should verify the generated `ToolPermissionRule` instances have correct `ToolPattern`, `Behavior`, `Priority`, and `Source` (should be `PermissionRuleSource.AutonomyTier`).

```csharp
// Test: GetRulesAsync_RestrictedTier_GeneratesGlobalAskRule
//   Arrange: Mock IAutonomyTierResolver returning Restricted for any agent.
//            Config with TierPolicies["Restricted"] = { DefaultBehavior = "Ask" }
//   Act: provider.GetRulesAsync("agent-1")
//   Assert: Returns single rule with ToolPattern "*", Behavior == Ask, Priority == 0,
//           Source == PermissionRuleSource.AutonomyTier

// Test: GetRulesAsync_SupervisedTier_GeneratesGlobalAskRule
//   Arrange: Same setup but Supervised tier and TierPolicies["Supervised"]
//   Assert: Same as Restricted -- both generate Ask baseline

// Test: GetRulesAsync_AutonomousTier_GeneratesGlobalAllowRule
//   Arrange: Autonomous tier, TierPolicies["Autonomous"] = { DefaultBehavior = "Allow" }
//   Assert: Returns Allow rule with pattern "*", Priority 0

// Test: GetRulesAsync_WithToolOverrides_GeneratesOverrideRulesAtHigherPriority
//   Arrange: Restricted tier + TierPolicies["Restricted"] = {
//              DefaultBehavior = "Ask",
//              ToolOverrides = { "query_kg": "Allow" }
//            }
//   Assert: Returns 2 rules:
//           (1) global Ask at Priority 0 for "*"
//           (2) specific Allow for "query_kg" at Priority 10

// Test: GetRulesAsync_NoTierPolicy_UsesDefaultBehavior
//   Arrange: Config has no TierPolicies entry for the agent's tier (e.g., tier is Supervised
//            but TierPolicies dictionary is empty)
//   Assert: Falls back to PermissionsConfig.DefaultBehavior (typically "Ask")
```

**Design note:** The provider's `Source` property should return `PermissionRuleSource.AutonomyTier` (new enum value from Section 1). The `GetRulesAsync` method receives `agentId` (string), but the implementation needs the `SubagentType` to resolve the tier. The provider resolves this through the injected `IAutonomyTierResolver`. For tests, the resolver mock controls the returned tier directly.

---

### 4. ThreePhasePermissionResolver Autonomy Integration Tests

**File:** `src/Content/Tests/Infrastructure.AI.Tests/Permissions/ThreePhasePermissionResolverAutonomyTests.cs`

**Namespace:** `Infrastructure.AI.Tests.Permissions`

Tests the end-to-end interaction between autonomy tier rules and the existing `ThreePhasePermissionResolver`. Uses the same `CreateResolver` helper pattern from `ThreePhasePermissionResolverTests.cs`, but with an `AutonomyTierRuleProvider` as one of the rule providers.

**Setup pattern:** Create a real `ThreePhasePermissionResolver` with multiple `IPermissionRuleProvider` instances -- one being the `AutonomyTierRuleProvider` (with mocked `IAutonomyTierResolver`) and optionally another providing manifest/session rules.

```csharp
// Test: RestrictedAgent_AnyTool_ReturnsAsk
//   Arrange: AutonomyTierRuleProvider generates Ask("*") at Priority 0.
//            No other rule providers.
//   Act: resolver.ResolvePermissionAsync("agent-1", "bash")
//   Assert: Decision.Behavior == Ask

// Test: RestrictedAgent_WithAllowOverride_SpecificToolAllowed
//   Arrange: AutonomyTierRuleProvider generates Ask("*") at Priority 0
//            AND Allow("query_knowledge_graph") at Priority 10
//   Act: resolver.ResolvePermissionAsync("agent-1", "query_knowledge_graph")
//   Assert: Decision.Behavior == Allow
//   Note: Phase 3 (Allow) evaluates the specific override, which has higher priority
//         than the global Ask, so the specific Allow wins.

// Test: AutonomousAgent_AnyTool_ReturnsAllow
//   Arrange: AutonomyTierRuleProvider generates Allow("*") at Priority 0
//   Act: resolver.ResolvePermissionAsync("agent-1", "file_system")
//   Assert: Decision.Behavior == Allow

// Test: AutonomousAgent_WithManifestDenyRule_DenyWins
//   Arrange: AutonomyTierRuleProvider generates Allow("*") at Priority 0
//            PLUS a second provider with Deny("bash") at Priority 5 from AgentManifest source
//   Act: resolver.ResolvePermissionAsync("agent-1", "bash")
//   Assert: Decision.Behavior == Deny
//   Note: Phase 1 (Deny) runs before Phase 3 (Allow). Deny always wins regardless of priority.

// Test: SupervisedAgent_WithSessionAllowOverride_SpecificToolAllowed
//   Arrange: AutonomyTierRuleProvider generates Ask("*") at Priority 0
//            PLUS a session override provider with Allow("file_system_read") at Priority 20
//   Act: resolver.ResolvePermissionAsync("agent-1", "file_system_read")
//   Assert: Decision.Behavior == Allow
```

**Important:** These tests validate the phase-based evaluation model. The `ThreePhasePermissionResolver` evaluates Deny rules first (Phase 1), then Ask rules (Phase 2), then Allow rules (Phase 3). Within each phase, rules are sorted by priority (lower first). A Deny match in Phase 1 short-circuits; an Ask match in Phase 2 short-circuits before Allow rules in Phase 3 are checked. The specific Allow override for a tool pattern works because the resolver finds the more specific pattern match within the Allow phase.

---

### 5. CapabilityMatchStrategy Tests

**File:** `src/Content/Tests/Infrastructure.AI.Tests/Agents/CapabilityMatchStrategyTests.cs`

**Namespace:** `Infrastructure.AI.Tests.Agents`

Tests `CapabilityMatchStrategy` (from Section 5, `Infrastructure.AI/Agents/CapabilityMatchStrategy.cs`). This is a pure function over in-memory data -- no mocks except `IOptionsMonitor<AppConfig>` for weight configuration.

**Helper method:** Create a `SupervisorDecisionContext` builder that takes candidates, required capabilities, minimum tier, and delegation depth as parameters.

```csharp
// === Filtering Phase ===

// Test: SelectAgent_AgentBelowMinimumTier_FilteredOut
//   Arrange: Single agent at Restricted, minimum tier Supervised
//   Act: strategy.SelectAgent(context)
//   Assert: Returns null (no candidates survive filtering)

// Test: SelectAgent_AgentLacksAllRequiredTools_FilteredOut
//   Arrange: Agent has [tool_a], required [tool_b, tool_c] -- zero overlap
//   Act: strategy.SelectAgent(context)
//   Assert: Returns null

// Test: SelectAgent_NoCandidatesAfterFiltering_ReturnsNull
//   Arrange: Empty AvailableAgents list
//   Assert: Returns null

// === Scoring Phase ===

// Test: SelectAgent_ToolCoverage_ScoresCorrectly
//   Arrange: Required [a, b, c], Agent has [a, b]
//   Assert: Agent's CapabilityScore.ToolCoverage == 2.0/3.0 (approximately 0.667)

// Test: SelectAgent_TypeAlignment_ExactMatch_ScoresOne
//   Arrange: Task description contains "search" keywords -> maps to Explore.
//            Agent type is Explore.
//   Assert: TypeAlignment == 1.0

// Test: SelectAgent_TypeAlignment_General_ScoresHalf
//   Arrange: Agent type is General (for any task type)
//   Assert: TypeAlignment == 0.5

// Test: SelectAgent_TierHeadroom_HigherTierScoresMore
//   Arrange: MinTier=Restricted, Agent1=Supervised (headroom = (1-0+1)/(2+1) = 0.667),
//            Agent2=Autonomous (headroom = (2-0+1)/(2+1) = 1.0)
//   Assert: Agent2 has higher TierHeadroom

// === Selection Phase ===

// Test: SelectAgent_TiedScore_PrefersLowerTier
//   Arrange: Two agents with identical tool coverage, type alignment, and tier headroom
//            but different actual tiers (one Supervised, one Autonomous)
//   Assert: Supervised agent selected (least privilege tiebreaker)

// Test: SelectAgent_SingleCandidate_SkipsScoring
//   Arrange: Only one agent survives filtering
//   Act: strategy.SelectAgent(context)
//   Assert: Returns that agent directly (ConfidenceScore may be 1.0 or the actual score)

// Test: SelectAgent_WeightsNormalized_SumNotOne
//   Arrange: Weights configured as ToolCoverage=0.4, TypeAlignment=0.3, TierHeadroom=0.5 (sum 1.2)
//   Assert: All computed TotalScore values fall within 0.0-1.0 range
//   Note: Constructor normalizes weights by dividing each by the total sum

// === Task Keyword Classifier ===

// Test: ClassifyTask_SearchKeywords_MapsToExplore
//   Arrange: "search for documentation about the API"
//   Assert: classified type == SubagentType.Explore

// Test: ClassifyTask_CreateKeywords_MapsToExecute
//   Arrange: "create a new service and write tests"
//   Assert: classified type == SubagentType.Execute

// Test: ClassifyTask_MixedKeywords_MostMatchesWins
//   Arrange: "search, find, and then create a report"
//   Assert: classified type == SubagentType.Explore (2 Explore keywords vs 1 Execute)

// Test: ClassifyTask_TiedKeywords_PrefersExecute
//   Arrange: "search and create"
//   Assert: classified type == SubagentType.Execute (tie broken by bias toward action)

// Test: ClassifyTask_NoKeywords_MapsToGeneral
//   Arrange: "do something about the thing"
//   Assert: classified type == SubagentType.General
```

**Keyword mapping reference (from Section 5):**
- search, find, read, explore, analyze -> Explore
- plan, design, architect, structure -> Plan
- test, verify, check, validate -> Verify
- execute, run, build, create, write, modify -> Execute
- (none match) -> General

---

### 6. CapabilityMatchSupervisor Tests

**File:** `src/Content/Tests/Infrastructure.AI.Tests/Agents/CapabilityMatchSupervisorTests.cs`

**Namespace:** `Infrastructure.AI.Tests.Agents`

Tests `CapabilityMatchSupervisor` (from Section 7, `Infrastructure.AI/Agents/CapabilityMatchSupervisor.cs`). This is the most mock-heavy test class -- it coordinates strategy, store, registry, tool resolver, tier resolver, audit, and agent execution.

**Mocks needed:**
- `ISupervisorStrategy` (keyed as `"capability-match"`)
- `IDelegationStore`
- `ISubagentProfileRegistry`
- `ISubagentToolResolver` (existing interface, `Application.AI.Common.Interfaces.Agents`)
- `IAutonomyTierResolver`
- `IGovernanceAuditService`
- `IOptionsMonitor<AppConfig>` (for `MaxDelegationDepth`, `DelegationTimeoutSeconds`, `MaxConcurrentDelegations`)
- `ILogger<CapabilityMatchSupervisor>`

**Setup notes:** The supervisor needs to construct an `AgentExecutionContext` for the delegate agent. Tests should mock the agent execution step (however the supervisor invokes it -- likely via a factory or delegate). The exact execution mechanism depends on how Section 7 implements agent invocation; tests should verify the supervisor's coordination logic, not the agent's execution.

```csharp
// === Happy Path ===

// Test: DelegateAsync_SuccessfulDelegation_ReturnsDelegationResultSuccess
//   Arrange: Strategy returns valid AgentSelection, execution returns success output
//   Assert: Result.IsSuccess == true, Result.Output is populated

// Test: DelegateAsync_RecordsPendingThenCompletedToStore
//   Arrange: Same happy path setup
//   Assert: IDelegationStore.AppendAsync called exactly twice:
//           first with State=Pending, then with State=Completed
//   Verify via Moq Callback or ordered verification

// Test: DelegateAsync_EmitsAuditEvents
//   Assert: IGovernanceAuditService.Log called at least twice
//           (once for delegation decision, once for outcome)

// === Failure Paths ===

// Test: DelegateAsync_DepthExceeded_ReturnsFailWithDepthExceededReason
//   Arrange: Set AgentExecutionContext.DelegationDepth to MaxDelegationDepth (e.g., 3)
//            so CurrentDelegationDepth >= MaxDelegationDepth
//   Assert: Result.IsSuccess == false, Result.FailureReason contains "depth"

// Test: DelegateAsync_NoCapableAgent_ReturnsFailWithNoAgentReason
//   Arrange: Strategy.SelectAgent returns null
//   Assert: Result.IsSuccess == false

// Test: DelegateAsync_AgentFailsWithAutonomyExceeded_PropagatesResult
//   Arrange: Agent execution returns DelegationResult.FailAutonomyExceeded(...)
//   Assert: Result.AutonomyExceeded is populated with the original exceeded info

// Test: DelegateAsync_AgentTimesOut_ReturnsFail
//   Arrange: Configure DelegationTimeoutSeconds = 0 (or very small) to force timeout
//   Assert: Result.IsSuccess == false

// === Tool Overrides ===

// Test: DelegateAsync_WithToolOverrides_OverridesAppliedToAgentContext
//   Arrange: Pass toolOverrides = ["extra_tool"] to DelegateAsync
//   Assert: The AgentExecutionContext built for the delegate includes the override tools

// === Concurrency ===

// Test: DelegateAsync_MaxConcurrentReached_BlocksUntilSlotFree
//   Arrange: MaxConcurrentDelegations = 1
//   Act: Start two delegations concurrently (first is slow, second should block)
//   Assert: Both complete without error, second starts after first releases semaphore
//   Note: Use TaskCompletionSource to control timing in test

// === Cancellation ===

// Test: CancelDelegationAsync_ActiveDelegation_PropagatesCancellation
//   Arrange: Start a delegation that blocks on a TaskCompletionSource
//   Act: Call CancelDelegationAsync with the delegation's ID
//   Assert: The delegation's CancellationToken is triggered, final state is Cancelled

// Test: CancelDelegationAsync_UnknownDelegationId_ReturnsFalse
//   Assert: CancelDelegationAsync(Guid.NewGuid()) returns false

// === Multi-Level Delegation ===

// Test: DelegateAsync_SetsDepthPlusOneInChildContext
//   Arrange: Parent supervisor has DelegationDepth = 1
//   Assert: Child AgentExecutionContext.DelegationDepth == 2
//   Verify via the context passed to agent execution (captured via mock callback)

// Test: DelegateAsync_SetsParentDelegationIdInChildRecord
//   Arrange: Parent supervisor has DelegationId = some Guid
//   Assert: Child DelegationRecord.ParentDelegationId == parent's DelegationId
//   Verify via the DelegationRecord passed to IDelegationStore.AppendAsync
```

---

### 7. JsonlDelegationStore Tests

**File:** `src/Content/Tests/Infrastructure.AI.Tests/Agents/JsonlDelegationStoreTests.cs`

**Namespace:** `Infrastructure.AI.Tests.Agents`

Tests `JsonlDelegationStore` (from Section 6, `Infrastructure.AI/Agents/JsonlDelegationStore.cs`). Uses real filesystem I/O with temp directories, following the `JsonlAgentHistoryStoreTests` pattern.

**Setup/teardown:** Implement `IDisposable`. Constructor creates a temp directory; `Dispose` deletes it recursively. The store is constructed with a configuration pointing to the temp directory.

**Helper method:** `BuildDelegationRecord(...)` factory for creating test `DelegationRecord` instances with sensible defaults.

```csharp
// Class implements IDisposable for temp directory cleanup
// private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"delegation-store-tests-{Guid.NewGuid():N}");

// === Round-Trip ===

// Test: AppendAsync_ThenGetByIdAsync_ReturnsRecord
//   Arrange: Create DelegationRecord with known DelegationId, State=Pending
//   Act: AppendAsync(record), then GetByIdAsync(delegationId)
//   Assert: Returned record matches original (DelegationId, SupervisorId, TaskDescription, State)

// Test: AppendAsync_MultipleTimes_GetByIdAsync_ReturnsLatestState
//   Arrange: Append Pending record, then append Completed record for same DelegationId
//   Act: GetByIdAsync(delegationId)
//   Assert: Returns the Completed record (latest state wins via deduplication)

// === Query Methods ===

// Test: GetBySessionAsync_ReturnsAllRecordsDeduplicatedById
//   Arrange: Append 3 delegations, each with 2 state changes (6 total lines).
//            All share the same SupervisorId.
//   Act: GetBySessionAsync(supervisorId)
//   Assert: Returns exactly 3 records (latest state for each DelegationId)

// Test: GetByParentAsync_ReturnsOnlyChildDelegations
//   Arrange: One parent delegation (ParentDelegationId=null) + 2 child delegations
//            (ParentDelegationId=parentId)
//   Act: GetByParentAsync(parentId)
//   Assert: Returns exactly 2 child records

// === Filesystem Behavior ===

// Test: AppendAsync_CreatesDirectoryStructureLazily
//   Arrange: Point store at non-existent subdirectory path
//   Act: AppendAsync(record)
//   Assert: Directory now exists, file is present

// Test: AppendAsync_FilePathContainsSupervisorIdAndTimestamp
//   Act: AppendAsync(record) with SupervisorId="supervisor-alpha"
//   Assert: File exists under {tempDir}/supervisor-alpha/ and filename contains a timestamp pattern

// === Concurrency ===

// Test: AppendAsync_ConcurrentWrites_NoCorruption
//   Arrange: 10 parallel AppendAsync calls with different DelegationIds
//   Act: Task.WhenAll(parallelAppends)
//   Assert: All records readable via GetBySessionAsync, no corrupted lines
//   Pattern: Read all lines from file, verify each is valid JSON

// === Error Handling ===

// Test: GetByIdAsync_PartialJsonLine_SkipsWithoutCrash
//   Arrange: AppendAsync a valid record, then write "NOT_VALID_JSON\n" directly to file,
//            then AppendAsync another valid record
//   Act: GetBySessionAsync(supervisorId)
//   Assert: Returns 2 valid records, corrupted line skipped silently
```

**Key implementation detail:** The store uses per-file `SemaphoreSlim(1, 1)` for thread safety, stored in a bounded LRU `ConcurrentDictionary` (max 100 entries). Tests of concurrent writes verify that the semaphore prevents partial-line interleaving. The concurrent writes test mirrors the existing `AppendAsync_ConcurrentAppends_DoNotCorruptFile` test in `JsonlAgentHistoryStoreTests.cs`.

---

### 8. DI Registration Tests

**File:** Additions to `src/Content/Tests/Infrastructure.AI.Tests/DependencyInjectionTests.cs`

**Namespace:** `Infrastructure.AI.Tests`

New test methods added to the existing `DependencyInjectionTests` class. Follow the existing pattern of `CreateBaseServices()` -> `AddInfrastructureAIDependencies()` -> `BuildServiceProvider()` -> resolve.

```csharp
// Test: ServiceProvider_ResolvesIAutonomyTierResolver
//   Act: provider.GetService<IAutonomyTierResolver>()
//   Assert: Not null

// Test: ServiceProvider_ResolvesISupervisor
//   Act: provider.GetService<ISupervisor>()
//   Assert: Not null

// Test: ServiceProvider_ResolvesIDelegationStore
//   Act: provider.GetService<IDelegationStore>()
//   Assert: Not null

// Test: ServiceProvider_ResolvesISupervisorStrategy_ByKey_CapabilityMatch
//   Act: provider.GetKeyedService<ISupervisorStrategy>("capability-match")
//   Assert: Not null

// Test: ServiceProvider_ResolvesIPermissionRuleProvider_IncludesAutonomyTierProvider
//   Act: provider.GetServices<IPermissionRuleProvider>()
//   Assert: Collection contains an instance of AutonomyTierRuleProvider
//   Note: This requires Application.Core DI also registered. If tests only call
//         AddInfrastructureAIDependencies, the AutonomyTierRuleProvider (registered in
//         Application.Core/DependencyInjection.cs) won't be present. Either add both
//         registrations in the test, or create a separate test that calls both DI methods.
```

**Config normalization test** (can go in this file or a dedicated config test file):

```csharp
// Test: CapabilityMatchWeightsConfig_NormalizesOnConstruction
//   Arrange: Create config with ToolCoverage=0.4, TypeAlignment=0.3, TierHeadroom=0.5 (sum=1.2)
//   Assert: After normalization, values sum to approximately 1.0
//           (ToolCoverage ~= 0.333, TypeAlignment ~= 0.25, TierHeadroom ~= 0.417)
```

---

## Integration Test Files

### 9. Full Pipeline Integration Tests

**File:** `src/Content/Tests/Infrastructure.AI.Tests/Integration/AutonomyPipelineIntegrationTests.cs`

**Namespace:** `Infrastructure.AI.Tests.Integration`

End-to-end tests that wire up the full permission pipeline with autonomy tier rules, verifying the behavior through MediatR or direct `ThreePhasePermissionResolver` calls with real (not mocked) rule providers.

**Setup complexity:** These tests require more wiring than unit tests. They construct a service collection with both `Infrastructure.AI` and `Application.Core` DI registrations, configure `AppConfig` with tier policies, and verify the full pipeline behavior.

```csharp
// Test: RestrictedAgent_ToolCall_ReturnsPermissionRequired
//   Setup: Full DI container with AutonomyTierRuleProvider registered.
//          AppConfig.AI.Permissions.TierPolicies["Restricted"] = { DefaultBehavior = "Ask" }
//          Mock IAutonomyTierResolver returns Restricted for the test agent.
//   Act: Resolve permission for agent "restricted-agent-1" calling tool "bash"
//   Assert: Decision.Behavior == Ask (PermissionRequired in the tool pipeline)

// Test: AutonomousAgent_ToolCall_Proceeds
//   Setup: Same, but TierPolicies["Autonomous"] = { DefaultBehavior = "Allow" }
//          Mock IAutonomyTierResolver returns Autonomous.
//   Act: Resolve permission for "autonomous-agent-1" calling "bash"
//   Assert: Decision.Behavior == Allow

// Test: Supervisor_DelegatesToAgent_AgentHitsAutonomyCeiling_SupervisorGetsStructuredFailure
//   Setup: Wire CapabilityMatchSupervisor with a mock strategy that selects a Restricted agent.
//          Mock agent execution to return DelegationResult.FailAutonomyExceeded(...)
//   Act: supervisor.DelegateAsync("write to filesystem", ["file_system"], Supervised)
//   Assert: DelegationResult.IsSuccess == false
//           DelegationResult.AutonomyExceeded is populated
//           DelegationResult.AutonomyExceeded.CurrentLevel == Restricted

// Test: Supervisor_MultiLevelDelegation_DepthLimitEnforced
//   Setup: MaxDelegationDepth = 2 in config.
//          Wire supervisor so it tracks delegation depth.
//   Act: Simulate: supervisor (depth 0) -> agent (depth 1) -> sub-agent tries to delegate (depth 2)
//   Assert: Third-level delegation at depth 2 fails with FailureReason containing "depth"
//   Note: This test may need to call DelegateAsync twice in sequence, incrementing depth
//         each time via the AgentExecutionContext, or use a custom mock that chains delegations.
```

**Implementation note on multi-level test:** The depth enforcement test is the trickiest. The supervisor reads `DelegationDepth` from its own `AgentExecutionContext`. For the first call, depth is 0 or null. For nested calls, the supervisor must be constructed or configured with a context that has `DelegationDepth` already set. The test verifies that when `DelegationDepth >= MaxDelegationDepth`, the supervisor short-circuits with a depth exceeded failure.

---

## File Summary

| File | Type | Component Under Test |
|------|------|---------------------|
| `Tests/Infrastructure.AI.Tests/Agents/DelegationResultTests.cs` | Unit | `DelegationResult` factories |
| `Tests/Infrastructure.AI.Tests/Governance/DefaultAutonomyTierResolverTests.cs` | Unit | `DefaultAutonomyTierResolver` |
| `Tests/Infrastructure.AI.Tests/Governance/AutonomyTierRuleProviderTests.cs` | Unit | `AutonomyTierRuleProvider` |
| `Tests/Infrastructure.AI.Tests/Permissions/ThreePhasePermissionResolverAutonomyTests.cs` | Unit | Permission resolver + autonomy rules |
| `Tests/Infrastructure.AI.Tests/Agents/CapabilityMatchStrategyTests.cs` | Unit | `CapabilityMatchStrategy` |
| `Tests/Infrastructure.AI.Tests/Agents/CapabilityMatchSupervisorTests.cs` | Unit | `CapabilityMatchSupervisor` |
| `Tests/Infrastructure.AI.Tests/Agents/JsonlDelegationStoreTests.cs` | Unit | `JsonlDelegationStore` |
| `Tests/Infrastructure.AI.Tests/DependencyInjectionTests.cs` | DI | New test methods (additive) |
| `Tests/Infrastructure.AI.Tests/Integration/AutonomyPipelineIntegrationTests.cs` | Integration | Full pipeline E2E |

All paths are relative to `src/Content/`. Full absolute base path: `C:\Users\kruz7\OneDrive\Documents\Code Repos\MCKRUZ\microsoft-agentic-harness\src\Content\`.

---

## Conventions to Follow

These conventions are derived from existing test files in the project:

1. **Naming:** `MethodName_Scenario_ExpectedResult` (e.g., `SelectAgent_AgentBelowMinimumTier_FilteredOut`)
2. **Structure:** Arrange-Act-Assert with blank lines separating sections
3. **Assertions:** FluentAssertions (`.Should().Be()`, `.Should().NotBeNull()`, `.Should().BeEmpty()`, `.Should().HaveCount()`, `.Should().Contain()`)
4. **Mocking:** Moq with `Mock<T>` fields initialized in constructor, `Mock.Of<T>()` for simple stubs
5. **Options pattern:** Use `Mock.Of<IOptionsMonitor<AppConfig>>(o => o.CurrentValue == appConfig)` or the `OptionsMonitorStub` inner class from `DependencyInjectionTests`
6. **Temp files:** Create temp directory in constructor with `Path.Combine(Path.GetTempPath(), $"prefix-{Guid.NewGuid():N}")`, delete in `Dispose()`
7. **Async:** Tests returning `Task` with `async`/`await`, no `.Result` or `.Wait()` calls
8. **Namespace:** Match the folder structure under `Infrastructure.AI.Tests` (e.g., `Infrastructure.AI.Tests.Agents`, `Infrastructure.AI.Tests.Governance`)
