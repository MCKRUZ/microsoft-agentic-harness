# Code Review: Section 17 — Governance Integration

## Summary
10 files reviewed (4 production, 6 test). The governance-escalation integration is structurally sound but has one correctness bug and two resilience gaps.

## Findings

### CRITICAL-1: ResolveEscalationWaitBehavior returns wrong tier's behavior
**File:** `GovernancePolicyBehavior.cs:171-183`
**Issue:** Iterates ALL tier policies and returns the first parseable `EscalationBehavior`, regardless of which tier the current agent belongs to. If `Restricted` tier has `QueueAndContinue` and `Supervised` has `Block`, a Supervised agent could get QueueAndContinue behavior.
**Impact:** Security — agent could bypass blocking escalation by inheriting a permissive tier's config.

### HIGH-1: No error handling around IEscalationService calls
**File:** `GovernancePolicyBehavior.cs:136,150` and `CapabilityMatchSupervisor.cs` escalation path
**Issue:** If `RequestEscalationAsync` or `QueueEscalationAsync` throws (network failure, timeout, service down), the exception propagates unhandled. This is fail-open — the governance check crashes instead of denying.
**Impact:** Resilience — escalation service outage bypasses governance.

### HIGH-2: Null-forgiving operator on _escalationService
**File:** `CapabilityMatchSupervisor.cs` — `HandleAutonomyEscalationAsync`
**Issue:** Uses `_escalationService!` null-forgiving operator. The guard check ensures it's not null at call site, but the method itself doesn't verify.
**Fix:** Use pattern matching or add null check in method.

### MEDIUM-1: Hardcoded RiskLevel.Medium everywhere
**File:** All three production files
**Issue:** `RiskLevel.Medium` is hardcoded. No way to derive risk from the governance decision or tool metadata.

### MEDIUM-2: Duplicate EscalationRequest construction
**File:** `GovernancePolicyBehavior.cs` and `CreateApprovalRequestExecutor.cs`
**Issue:** Nearly identical EscalationRequest object initialization in two places.

### MEDIUM-3: CreateApprovalRequestExecutor silent failure
**File:** `CreateApprovalRequestExecutor.cs:49-66`
**Issue:** If `QueueEscalationAsync` throws, the executor still returns the ApprovalRequest as if nothing happened. The escalation silently fails.

### MEDIUM-4: No audit logging in CapabilityMatchSupervisor escalation path
**File:** `CapabilityMatchSupervisor.cs`
**Issue:** Escalation triggered/approved/denied but no audit trail via IGovernanceAuditService.
