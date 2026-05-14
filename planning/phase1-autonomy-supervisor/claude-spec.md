# Complete Specification — Phase 1: Autonomy Tiers & Supervisor Agent

## Overview

Phase 1 adds two foundational capabilities to the Microsoft Agentic Harness:

1. **Autonomy Tiers** — A 3-tier trust model (Restricted / Supervised / Autonomous) that sets the default permission posture for any agent instance. Tiers are orthogonal to agent type — any `SubagentType` can operate at any tier.

2. **Supervisor Agent** — A Coordinator-Worker pattern implementation that delegates tasks to specialist agents via deterministic capability matching, tracks delegation state in persisted JSONL, and supports multi-level delegation with configurable max depth.

These capabilities bridge the gap between "smart RAG system" and "agentic platform" — enabling the harness to decompose goals into delegated tasks with explicit trust boundaries.

---

## Feature 1: Autonomy Tiers

### Tier Definitions

| Tier | Name | Default Permission | Behavior |
|------|------|--------------------|----------|
| 0 | **Restricted** | Deny | Read-only. Can observe, query, report. Cannot modify state, call external APIs, or execute tools that write. Every action beyond reads requires explicit override. |
| 1 | **Supervised** | Ask | Recommend-and-wait. Can perform reads freely. Write/execute actions require approval (returned as `PermissionRequired`). The caller (supervisor or human) decides. |
| 2 | **Autonomous** | Allow | Act within guardrails. Can execute any tool allowed by its profile. Safety gates and AGT policy engine still apply — autonomous does not mean unchecked. |

### Design Decisions

- **Orthogonal to SubagentType**: An Explore agent can be Restricted (read-only filesystem browsing) or Autonomous (full filesystem access). Tier is assigned per-agent-instance via `SubagentDefinition` or `AgentExecutionContext`.
- **Tiers set defaults, not absolutes**: Individual `ToolPermissionRule` entries can override the tier baseline. A Restricted agent could have an explicit Allow rule for a specific safe tool.
- **Independent from AGT trust scoring**: AGT evaluates individual tool calls. Our tiers set the posture. They don't feed into each other — clean separation of concerns.
- **Auditable tier assignment**: Every tier assignment and change emits to `IGovernanceAuditService`.

### Integration with Permission System

The existing `ThreePhasePermissionResolver` uses pluggable `IPermissionRuleProvider` instances to gather rules. Autonomy tiers integrate by adding a new provider:

1. New `AutonomyTierRuleProvider` implements `IPermissionRuleProvider`
2. Given an agent's `AutonomyLevel`, generates baseline `ToolPermissionRule` entries:
   - Restricted → global Deny rule at low priority (overridable by higher-priority Allow rules)
   - Supervised → global Ask rule at low priority
   - Autonomous → global Allow rule at low priority
3. These baseline rules participate in the existing 3-phase resolution — they don't bypass it
4. Higher-priority rules from other sources (agent manifest, skill definition, session override) take precedence

### Domain Models (new)

- `AutonomyLevel` enum: `Restricted = 0`, `Supervised = 1`, `Autonomous = 2`
- `AutonomyTierPolicy` record: Maps an `AutonomyLevel` to default permission behavior + optional tool-specific overrides
- `AutonomyExceededResult` record: Structured error when an action exceeds the agent's tier ceiling — includes the attempted action, current tier, required tier

### Interfaces (new)

- `IAutonomyTierResolver`: Given an agent ID/context, resolves the effective `AutonomyLevel`. Default implementation reads from `SubagentDefinition.AutonomyLevel` property.

### Configuration

```json
{
  "AI": {
    "Permissions": {
      "DefaultAutonomyLevel": "Supervised",
      "TierPolicies": {
        "Restricted": { "DefaultBehavior": "Deny" },
        "Supervised": { "DefaultBehavior": "Ask" },
        "Autonomous": { "DefaultBehavior": "Allow" }
      }
    }
  }
}
```

### Autonomy Ceiling Behavior

When a subagent attempts an action beyond its tier:
- `ThreePhasePermissionResolver` returns `PermissionDecision` with `Behavior = Deny` or `Ask`
- `ToolPermissionBehavior` (MediatR pipeline position 6) maps this to `ResultFailureType.Forbidden` or `ResultFailureType.PermissionRequired`
- The result includes `AutonomyExceededResult` with details about what was attempted and what tier would be needed
- **No automatic escalation** — the caller (supervisor, orchestrator, or user) receives the structured error and decides next steps
- Phase 2 (Human Escalation) will add the escalation path

---

## Feature 2: Supervisor Agent

### Pattern: Coordinator-Worker

The supervisor receives a high-level task, decomposes it (or receives pre-decomposed subtasks), and delegates each to the best-fit agent based on deterministic capability matching. It tracks delegation state and aggregates results.

### Design Decisions

- **Deterministic capability matching**: No LLM reasoning for agent selection. Match task requirements (tool needs, domain tags, complexity) to agent capabilities (tools, type, tier) via scored rules. Fast, predictable, auditable, testable.
- **Immutable profiles + per-delegation overrides**: `BuiltInSubagentProfiles` stay immutable. The supervisor can pass per-delegation tool overrides that layer on top (e.g., "for this task, also allow `web_search`"). Overrides are scoped to the single delegation, not persisted to the profile.
- **Multi-level delegation with max depth**: Agents can delegate to sub-agents (supervisor → agent A → sub-agent B). Configurable `MaxDelegationDepth` (default: 3) prevents infinite recursion. Each delegation increments a depth counter passed through context.
- **Persisted delegation state**: JSONL file per supervisor session, consistent with `JsonlAgentHistoryStore` pattern. Append-only, one record per delegation state change.
- **Fail with structured error on autonomy ceiling**: When a delegated agent hits its tier ceiling, the delegation fails with `DelegationState.Failed` and an `AutonomyExceededResult`. The supervisor receives this and can re-delegate to a higher-tier agent or return the failure upstream.

### Domain Models (new)

- `DelegationState` enum: `Pending`, `InProgress`, `Completed`, `Failed`, `Cancelled`
- `DelegationRecord` record: Immutable record — DelegationId, ParentDelegationId (for nesting), SupervisorId, DelegateAgentId, TaskDescription, RequiredCapabilities, ToolOverrides, AutonomyLevel, State, Result, StartedAt, CompletedAt, DelegationDepth
- `DelegationResult` record: Outcome of a delegation — IsSuccess, Output, FailureReason (including AutonomyExceeded), TokensUsed, DurationMs
- `SupervisorDecisionContext` record: Task requirements + available agents + their capabilities/tiers — input to the strategy
- `CapabilityScore` record: Scored match between task requirements and agent capabilities

### Interfaces (new)

- `ISupervisor`: Core coordination contract
  - `DelegateAsync(task, context)` → `DelegationResult`
  - `GetDelegationStatus(delegationId)` → `DelegationRecord`
  - `GetActiveDelegations()` → `IReadOnlyList<DelegationRecord>`
  - `CancelDelegation(delegationId)` → bool
- `ISupervisorStrategy`: Pluggable agent selection
  - `SelectAgent(decisionContext)` → `AgentSelection` (selected agent + confidence score + reasoning)
  - Keyed DI: `"capability-match"` (default), extensible for `"round-robin"`, `"load-balance"`, etc.
- `IDelegationStore`: Persistence for delegation records
  - `AppendAsync(record)`, `GetByIdAsync(delegationId)`, `GetBySessionAsync(sessionId)`, `GetByParentAsync(parentDelegationId)`

### Supervisor Execution Flow

```
1. Supervisor receives task + context
2. ISupervisorStrategy.SelectAgent(decisionContext) → picks best agent
3. Build delegation: merge agent profile + per-delegation tool overrides
4. Check delegation depth < MaxDelegationDepth
5. IDelegationStore.AppendAsync(record with State=Pending)
6. Execute agent via AgentExecutorFactory (MAF WorkflowBuilder substrate)
7. Monitor execution:
   a. Success → State=Completed, capture result
   b. AutonomyExceeded → State=Failed, return structured error
   c. MaxTurns exceeded → State=Failed, return timeout error
   d. Exception → State=Failed, return error details
8. IDelegationStore.AppendAsync(updated record)
9. Return DelegationResult to caller
```

### Capability Matching Algorithm

The default `CapabilityMatchStrategy`:

1. **Filter**: Remove agents that lack required tools (after applying delegation overrides)
2. **Filter**: Remove agents whose `AutonomyLevel` is below the minimum required for the task
3. **Score** remaining agents:
   - Tool coverage: % of required tools available (weight: 0.4)
   - Type alignment: Does `SubagentType` match the task category? (weight: 0.3)
   - Tier headroom: Higher tier = less likely to hit ceiling (weight: 0.2)
   - Historical success: Past delegation success rate for similar tasks (weight: 0.1)
4. **Select** highest-scoring agent. If tie, prefer lower tier (least privilege).
5. Return `AgentSelection` with agent, score, and reasoning string for audit

### Configuration

```json
{
  "AI": {
    "Orchestration": {
      "MaxDelegationDepth": 3,
      "DefaultSupervisorStrategy": "capability-match",
      "DelegationStoragePath": ".agent-sessions/delegations",
      "MaxConcurrentDelegations": 5,
      "DelegationTimeoutSeconds": 300
    }
  }
}
```

### Integration with Existing Systems

- **AgentExecutorFactory**: Creates executor bindings for delegated agents — supervisor doesn't manage LLM clients directly
- **MultiAgentWorkflow**: For fan-out delegations (multiple agents in parallel), supervisor builds a MAF workflow graph
- **IToolPermissionService**: Per-delegation tool overrides are resolved through the same 3-phase permission system
- **IGovernanceAuditService**: Every delegation decision (agent selected, delegation started, completed, failed) emits audit events
- **GovernanceMetrics**: OTel counters for delegations_total, delegations_failed, delegation_duration_ms, autonomy_exceeded_total

---

## Cross-Cutting Concerns

### Layer Placement

| Layer | New Types |
|-------|-----------|
| **Domain.AI** | `AutonomyLevel`, `AutonomyTierPolicy`, `AutonomyExceededResult`, `DelegationState`, `DelegationRecord`, `DelegationResult`, `SupervisorDecisionContext`, `CapabilityScore` |
| **Application.AI.Common** | `IAutonomyTierResolver`, `ISupervisor`, `ISupervisorStrategy`, `IDelegationStore` |
| **Application.Core** | `AutonomyTierRuleProvider` (IPermissionRuleProvider impl), MediatR behavior updates |
| **Infrastructure.AI** | `DefaultAutonomyTierResolver`, `CapabilityMatchSupervisor`, `CapabilityMatchStrategy`, `JsonlDelegationStore`, DI registration |

### Existing Types Modified

- `SubagentDefinition` — Add `AutonomyLevel` property (default: `Supervised`)
- `PermissionsConfig` — Add `DefaultAutonomyLevel` and `TierPolicies` section
- `SubagentConfig` — Add `MaxDelegationDepth`, `DelegationStoragePath`, `DelegationTimeoutSeconds`
- `DependencyInjection.cs` (Infrastructure.AI) — Register new services
- `DependencyInjection.cs` (Application.Core) — Register `AutonomyTierRuleProvider`

### Quality Requirements

- 80% test coverage on all new code
- Full XML documentation on all public types (template — docs are teaching material)
- Immutable records with init-only properties throughout
- Keyed DI for `ISupervisorStrategy` registrations
- Integration tests: permission resolution with autonomy tiers, supervisor delegation lifecycle, JSONL persistence round-trip
- Unit tests: capability matching algorithm, tier-to-rule generation, delegation depth limits
