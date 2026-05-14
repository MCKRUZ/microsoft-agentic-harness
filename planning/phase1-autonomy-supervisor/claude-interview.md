# Interview Transcript — Phase 1: Autonomy Tiers & Supervisor Agent

## Q1: Should autonomy tiers be orthogonal to subagent type, or should types imply a tier?

**Options presented:** Orthogonal (any agent type at any tier), Type implies default tier, Strict mapping.

**Answer:** Orthogonal. Any agent type can operate at any autonomy tier. Tier is assigned per-agent-instance, not per-type.

## Q2: How should the supervisor select which agent handles a task?

**Options presented:** Deterministic capability matching, Hybrid (deterministic + LLM fallback), LLM-based routing.

**Answer:** Deterministic capability matching. Match task requirements to agent capabilities via scored rules. Fast, predictable, auditable, testable.

## Q3: What happens when a subagent hits its autonomy ceiling?

**Options presented:** Escalate to supervisor, Fail with structured error, Configurable per tier.

**Answer:** Fail with structured error. Return a DelegationResult with AutonomyExceeded reason. Caller decides next step. Simplest approach — Phase 2 will add full human escalation.

## Q4: Should our AutonomyLevel integrate with AGT's trust scoring?

**Context clarified:** User asked for explanation of AGT (Microsoft's Agent Governance Toolkit — a NuGet dependency we wrap with adapter classes). AGT provides per-call policy evaluation, audit, MCP security. Our tiers define the default permission posture above AGT.

**Options presented:** Independent, AGT triggers downgrade only, Full integration.

**Answer:** Independent. Our tiers are explicit assignments. AGT trust scoring feeds into per-call governance decisions separately. Clean separation.

## Q5: Should DelegationRecord be persisted or in-memory?

**Options presented:** In-memory with serializable design, Persisted from day one, In-memory only.

**Answer:** Persisted from day one. Enables cross-session analytics and crash recovery.

## Q6: Should supervisors be able to pass per-delegation tool overrides?

**Options presented:** Immutable profiles + delegation overrides, Strictly immutable.

**Answer:** Immutable profiles + delegation overrides. Profiles stay as-is. Supervisor can grant extra tools per-delegation (e.g., "for this task, also allow web_search"). Clean separation of definition vs. runtime.

## Q7: What storage backend for delegation persistence?

**Options presented:** JSONL file per session, SQLite, JSON files per delegation.

**Answer:** JSONL file per session. Consistent with existing patterns (JsonlAgentHistoryStore). One file per supervisor session, append-only.

## Q8: What delegation depth should the supervisor support?

**Options presented:** Single level, Multi-level with max depth.

**Answer:** Multi-level with configurable max depth. Allow nested delegation (supervisor → agent A → sub-agent B) with a configurable max depth limit.
