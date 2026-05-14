# Integration Notes — Opus Review Feedback

## Integrating (fixes real bugs/gaps)

### Issue #1: agentId → SubagentDefinition mapping gap
**Integrating.** This is a real gap. ISubagentProfileRegistry is keyed by SubagentType, not agentId. Fix: Change IAutonomyTierResolver to accept SubagentType (or the full SubagentDefinition) instead of agentId string. The supervisor already knows the agent type when delegating.

### Issue #2: ThreePhasePermissionResolver phase conflict
**Integrating.** This is the most critical catch. The 3-phase algorithm evaluates Deny→Ask→Allow in order. A global Deny rule blocks everything regardless of priority. Fix: Instead of generating Deny rules for Restricted tier, generate rules that match the tier's intent using the correct phase:
- Restricted: Generate Ask rules (not Deny) at Priority 0. Safety gates handle true Deny. The Restricted posture is "ask for everything" which effectively blocks automated execution.
- Supervised: Generate Ask rules at Priority 0 (same behavior, explicit).
- Autonomous: Generate Allow rules at Priority 0 (baseline allow, overridable by higher-priority Deny rules from other sources).

This means Restricted and Supervised both generate Ask rules (different from my original "Restricted=Deny" mapping). The real enforcement difference is that Restricted agents have no tool overrides allowing anything, while Supervised agents may have specific Allow overrides for approved tools.

### Issue #3: GetByIdAsync semantics
**Integrating.** Document that GetByIdAsync returns the latest record for a given DelegationId. Internally, it reads all records for the ID and returns the one with the latest timestamp.

### Issue #6: Delegation depth via untyped AdditionalProperties
**Integrating.** Add a typed `DelegationDepth` property to `AgentExecutionContext` instead of using magic string keys.

### Issue #7: AgentExecutionContextFactory expects SkillDefinition
**Integrating.** Add a new factory method overload that accepts SubagentDefinition directly, bridging to AgentExecutionContext without requiring SkillDefinition.

### Issue #14: PermissionRuleSource.AutonomyTier
**Integrating.** Add new enum value `AutonomyTier` to PermissionRuleSource for audit distinguishability.

### Issue #15: TierPolicies config POCO
**Integrating.** Define `AutonomyTierPolicyConfig` class in Domain.Common/Config/.

### Issue #19: CapabilityMatchWeights validation
**Integrating.** Normalize weights at construction time so they always sum to 1.0.

### Issue #20: AutonomyLevel.MaxValue
**Integrating.** Replace with `(int)Enum.GetValues<AutonomyLevel>().Max()` or define a `const int MaxTierValue = 2`.

### Issue #12: Partial line reads on Windows
**Integrating.** Add JsonException catch for partial line reads, consistent with existing JsonlAgentHistoryStore pattern.

### Issue #8: Keyword classifier tie-breaking
**Integrating.** Define "most keyword matches wins" with Execute as tiebreaker (bias toward action).

## Deferring (valid but not blocking Phase 1)

### Issue #4: DelegateAsync decomposition
**Deferring.** Valid concern about method size. Will decompose into private methods during implementation, but the plan describes the logical flow correctly. The section-level guidance is sufficient.

### Issue #5: Lifecycle methods on ISupervisor
**Deferring.** GetActiveDelegationsAsync and CancelDelegationAsync are useful for the supervisor's callers. Splitting into IDelegationManager adds an interface for no immediate benefit. Can refactor later if the interface grows.

### Issue #9: HistoricalSuccessRate pre-load
**Integrating partially.** Remove HistoricalSuccessRate from the scoring algorithm for Phase 1. It's always null/0.5 and adds no signal. Add it back in Phase 3 (Learnings Log) when we have the aggregation infrastructure.

### Issue #10: Cross-session GetByIdAsync scanning
**Deferring.** For Phase 1, GetByIdAsync only searches the current session file. Cross-session lookup is a future optimization. Document this limitation.

### Issue #11: SemaphoreSlim memory leak
**Integrating.** Use a bounded LRU cache for semaphores instead of unbounded ConcurrentDictionary.

### Issue #13: CancelDelegationAsync propagation
**Integrating.** Store the CancellationTokenSource for each active delegation in a ConcurrentDictionary keyed by DelegationId. CancelDelegationAsync triggers the source.

### Issue #16: HistoricalSuccessRate collection
**Deferring to Phase 3.** Removing from scoring algorithm for now (see Issue #9).

### Issue #17: MaxConcurrentDelegations enforcement
**Integrating.** Add a SemaphoreSlim(maxConcurrent) at the top of DelegateAsync.

### Issue #18: appsettings.json example
**Integrating.** Add a complete example in the DI/Config section.

## Not Integrating

None — all feedback was valid. Everything is either integrated or explicitly deferred with rationale.
