# Phase 2 Interview Transcript

## Q1: Escalation Behavior While Waiting for Approval

**Question:** When an agent exceeds its autonomy tier and triggers escalation, how should the system behave while waiting for human approval?

**Answer:** Configurable per-tier. Restricted agents block and wait; Supervised agents queue the blocked action and continue other work. Behavior determined by policy config per autonomy level.

## Q2: Fallback Chain Transparency

**Question:** For the LLM fallback chain, should fallback be transparent to the agent or include metadata about which provider answered?

**Answer:** Metadata + agent adaptation. The agent receives degradation info (which provider, what capabilities are restricted) AND can adapt its behavior (simplify prompts, skip tool use that requires unavailable features).

## Q3: Approval Timeout Behavior

**Question:** When a pending approval expires without a human response, what should the default action be?

**Answer:** Auto-deny + escalate. Deny the current request so the agent isn't blocked indefinitely, but auto-escalate a notification to a higher tier for visibility. Default behavior is configurable per-policy.

## Q4: Notification/Delivery Mechanism

**Question:** Should we extend the AG-UI event system or add a parallel SignalR hub for governance notifications?

**Answer:** Both + adapter pattern. AG-UI events for the dashboard (consistent with existing architecture), plus an extensible adapter pattern (`IEscalationNotifier`) for external channels (Slack, Teams, email). The adapter decouples notification delivery from the escalation logic.

## Q5: Multi-Approver Strategies

**Question:** Which approval strategies should we implement in Phase 2?

**Answer:** All three in Phase 2 — any-of (first approver unblocks), all-of (every approver must approve), and quorum (N-of-M majority threshold). Build with strategy pattern so they're pluggable.

## Q6: Provider Health Checking

**Question:** Should health probes be passive, active, or hybrid?

**Answer:** Passive with pre-warm on circuit recovery. Track real-traffic failures to feed circuit breaker state. When a circuit transitions from Open to HalfOpen, send a synthetic probe before routing real traffic to validate recovery.

## Q7: Degraded Mode Behavior

**Question:** What's the minimum viable behavior when all LLM providers fail?

**Answer:** Graceful error + queue for retry. Return a structured error to the caller, queue the request for automatic retry when providers recover. No cached/static responses; no read-only mode.

## Q8: Approval Audit Trail

**Question:** JSONL store, structured logging, or both for audit?

**Answer:** Both. JSONL append-only store for queryable audit trail (consistent with Phase 1's JsonlDelegationStore pattern), plus structured logging via existing StructuredLogAuditSink for real-time observability.

## Q9: Notification Adapter Scope

**Question:** How many adapters should we implement for the escalation notification pattern?

**Answer:** Interface + AG-UI + no-op adapters. Define `IEscalationNotifier`, implement the AG-UI adapter for the dashboard, and add no-op stubs for Slack and Teams as extension points. This makes the extensibility pattern obvious to template consumers.
