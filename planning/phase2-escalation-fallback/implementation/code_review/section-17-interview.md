# Code Review Interview: Section 17

## Decisions

### CRITICAL-1: ResolveEscalationWaitBehavior wrong tier lookup
**Decision:** Fix — pass agent's actual tier to resolver. Look up from IAgentExecutionContext, fetch that tier's EscalationBehavior from PermissionsConfig.
**Action:** Refactor ResolveEscalationWaitBehavior to accept agentId, resolve tier, look up specific policy.

### HIGH-1: No error handling around IEscalationService
**Decision:** Fail-closed — deny on exception. Wrap escalation calls in try/catch, return GovernanceBlocked on failure.
**Action:** Add try/catch in GovernancePolicyBehavior.HandleRequireApprovalAsync and CapabilityMatchSupervisor.HandleAutonomyEscalationAsync.

## Auto-fixes

### HIGH-2: Null-forgiving operator
**Action:** Replace `_escalationService!` with pattern matching guard in HandleAutonomyEscalationAsync.

### MEDIUM-3: CreateApprovalRequestExecutor silent failure
**Action:** Add try/catch around QueueEscalationAsync, log warning on failure. Executor still returns ApprovalRequest (fire-and-forget intent preserved).

## Let go

### MEDIUM-1: Hardcoded RiskLevel.Medium — no risk metadata source exists yet
### MEDIUM-2: Duplicate EscalationRequest — different contexts, premature abstraction
### MEDIUM-4: No audit in supervisor — would expand constructor scope
