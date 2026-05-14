<!-- PROJECT_CONFIG
runtime: dotnet
test_command: dotnet test src/AgenticHarness.slnx
END_PROJECT_CONFIG -->

<!-- SECTION_MANIFEST
section-01-domain-autonomy
section-02-domain-delegation
section-03-interfaces
section-04-tier-rule-provider
section-05-capability-strategy
section-06-jsonl-delegation-store
section-07-supervisor-implementation
section-08-di-and-config
section-09-tests
END_MANIFEST -->

# Implementation Sections Index

## Dependency Graph

| Section | Depends On | Blocks | Parallelizable |
|---------|------------|--------|----------------|
| section-01-domain-autonomy | - | 03, 04 | Yes (with 02) |
| section-02-domain-delegation | - | 03, 05, 06, 07 | Yes (with 01) |
| section-03-interfaces | 01, 02 | 04, 05, 06, 07 | No |
| section-04-tier-rule-provider | 03 | 08 | Yes (with 05, 06) |
| section-05-capability-strategy | 03 | 07 | Yes (with 04, 06) |
| section-06-jsonl-delegation-store | 03 | 07 | Yes (with 04, 05) |
| section-07-supervisor-implementation | 03, 04, 05, 06 | 08 | No |
| section-08-di-and-config | 01-07 | 09 | No |
| section-09-tests | 01-08 | - | No |

## Execution Order

1. **Batch 1**: section-01-domain-autonomy, section-02-domain-delegation (parallel — no dependencies)
2. **Batch 2**: section-03-interfaces (requires 01 + 02)
3. **Batch 3**: section-04-tier-rule-provider, section-05-capability-strategy, section-06-jsonl-delegation-store (parallel — all depend only on 03)
4. **Batch 4**: section-07-supervisor-implementation (requires 03-06)
5. **Batch 5**: section-08-di-and-config (requires all)
6. **Batch 6**: section-09-tests (requires all)

## Section Summaries

### section-01-domain-autonomy
Domain primitives for autonomy tiers: `AutonomyLevel` enum, `AutonomyTierPolicy` record, `AutonomyExceededResult` record. Modify `SubagentDefinition` to add `AutonomyLevel` property. Add `AutonomyTier` to `PermissionRuleSource` enum.

### section-02-domain-delegation
Domain primitives for delegation: `DelegationState` enum, `DelegationRecord`, `DelegationResult`, `SupervisorDecisionContext`, `AgentCandidate`, `AgentSelection`, `CapabilityScore` records. All in `Domain.AI/Orchestration/`.

### section-03-interfaces
Application-layer contracts: `IAutonomyTierResolver`, `ISupervisor`, `ISupervisorStrategy`, `IDelegationStore`. Modify `AgentExecutionContext` to add typed `DelegationDepth`, `DelegationId`, `DelegatingAgentType` properties. Add `CreateFromDelegation` overload to `AgentExecutionContextFactory`.

### section-04-tier-rule-provider
`AutonomyTierRuleProvider` implementing `IPermissionRuleProvider`. Phase-aware rule generation (Ask for Restricted/Supervised, Allow for Autonomous). Tool overrides at higher priority. `DefaultAutonomyTierResolver` implementation.

### section-05-capability-strategy
`CapabilityMatchStrategy` implementing `ISupervisorStrategy`. Three-phase algorithm: filter by tier/tools, score by coverage/alignment/headroom (normalized weights), select with least-privilege tiebreaker. Keyword-based task classifier.

### section-06-jsonl-delegation-store
`JsonlDelegationStore` implementing `IDelegationStore`. Append-only JSONL per session. Latest-state deduplication on reads. Bounded LRU semaphore cache. Partial-line error handling.

### section-07-supervisor-implementation
`CapabilityMatchSupervisor` implementing `ISupervisor`. Coordinates strategy, execution, persistence, audit. Concurrency limiting via SemaphoreSlim. CancellationTokenSource per delegation. Multi-level depth tracking.

### section-08-di-and-config
DI registration in Infrastructure.AI and Application.Core. Config POCOs (`AutonomyTierPolicyConfig`, `CapabilityMatchWeightsConfig`). Config extensions for `PermissionsConfig` and `SubagentConfig`. OTel metrics. Example appsettings.json.

### section-09-tests
All unit tests, integration tests, and pipeline tests as defined in claude-plan-tdd.md. xUnit + Moq + FluentAssertions.
