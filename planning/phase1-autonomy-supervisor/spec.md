# Phase 1: Autonomy Tiers & Supervisor Agent

## Context

The Microsoft Agentic Harness is a production-grade template for building enterprise agent systems using Microsoft Agent Framework (MAF), Clean Architecture, CQRS/MediatR, and keyed DI. It currently has a strong RAG pipeline, knowledge graph, MCP client+server, governance (AGT-backed), and meta-harness self-optimization.

A gap analysis against an enterprise agentic platform architecture revealed that while the harness excels at document-driven agentic tasks, it lacks the orchestration backbone needed for true multi-agent coordination. Phase 1 addresses the two gaps with the highest existing foundation and most dependency value.

## Goal

Implement **Autonomy Tiers** and **Supervisor Agent** capabilities to transform the harness from a single-agent RAG system into a multi-agent platform with explicit permission levels and coordinated delegation.

---

## Gap 1: Autonomy Tiers (~30% remaining)

### Existing Foundations (70% complete)

| Component | Location |
|-----------|----------|
| `PermissionBehaviorType` enum (Allow, Deny, Ask) | `Domain.AI/Permissions/PermissionBehaviorType.cs` |
| `ToolPermissionRule` with Priority field | `Domain.AI/Permissions/ToolPermissionRule.cs` |
| `IToolPermissionService` — 3-phase resolution (Deny gates → Ask rules → Allow rules) | `Application.AI.Common/Interfaces/Agent/IToolPermissionService.cs` |
| `ToolPermissionFilter` service | `Infrastructure.AI/` |
| `IDenialTracker` for rate-limiting repeated denials | `Application.AI.Common/Interfaces/Agent/IDenialTracker.cs` |
| `SubagentDefinition` with PermissionMode, tool allowlist/denylist | `Domain.AI/Agents/SubagentDefinition.cs` |
| `IGovernancePolicyEngine` for policy evaluation | `Application.AI.Common/Interfaces/Governance/IGovernancePolicyEngine.cs` |

### What's Needed

1. **`AutonomyLevel` enum** in `Domain.AI/Governance/` — Defines explicit tiers (e.g., Restricted, Supervised, Collaborative, Autonomous) with clear semantics for each level
2. **`AutonomyTierPolicy` record** — Maps an `AutonomyLevel` to default tool permission sets (which tools are allowed/denied/require-approval at each tier)
3. **`IAutonomyTierResolver`** in `Application.AI.Common/Interfaces/Governance/` — Given an agent context, resolves the effective autonomy tier (could be from agent definition, runtime override, or policy engine)
4. **Integration with `IToolPermissionService`** — Autonomy tier should be consulted during the 3-phase permission resolution, acting as a baseline that individual tool rules can override
5. **Agent-level autonomy assignment** — Extend `AgentDefinition` or `SubagentDefinition` to carry an `AutonomyLevel` property
6. **Runtime autonomy checks** — MediatR pipeline behavior that enforces tier constraints before tool execution

### Design Constraints

- Tiers define *defaults*, not absolutes — individual `ToolPermissionRule` entries can override the tier baseline
- Must integrate with existing AGT policy engine (governance decisions may adjust tier at runtime)
- Tier changes should be auditable (emitted to `IAuditSink`)
- Configuration via `appsettings.json` for default tier mappings

---

## Gap 2: Supervisor Agent (~40% remaining)

### Existing Foundations (60% complete)

| Component | Location |
|-----------|----------|
| `SubagentType` enum (Autonomous, Collaborative, Supervised, Delegator) | `Domain.AI/Agents/SubagentType.cs` |
| `ISubagentToolResolver` with full implementation | `Application.AI.Common/Interfaces/Agents/ISubagentToolResolver.cs` |
| `ISubagentProfileRegistry` for agent discovery | `Application.AI.Common/Interfaces/Agents/ISubagentProfileRegistry.cs` |
| `BuiltInSubagentProfiles.cs` — Predefined profiles | `Infrastructure.AI/Agents/BuiltInSubagentProfiles.cs` |
| `MultiAgentWorkflow` — MAF multi-agent orchestration | `Application.Core/Workflows/Orchestration/MultiAgentWorkflow.cs` |
| `AgentExecutorFactory` | `Application.Core/Workflows/Orchestration/AgentExecutorFactory.cs` |
| `GovernanceApprovalWorkflow` with MAF RequestPort pattern | `Application.Core/Workflows/Governance/GovernanceApprovalWorkflow.cs` |

### What's Needed

1. **`ISupervisor` interface** in `Application.AI.Common/Interfaces/Agents/` — Multi-agent coordination contract: task assignment, delegation, result aggregation, escalation handling
2. **`SupervisorDecisionContext` record** — Captures task requirements, available agents, their capabilities and autonomy tiers — input to the decision function
3. **`DelegationState` enum** — Tracks lifecycle of delegated work: Pending, InProgress, Completed, Failed, Escalated
4. **`DelegationRecord` record** — Immutable record of a delegation: which task, to which agent, at what autonomy level, current state, result
5. **`ISupervisorStrategy` interface** — Pluggable strategy for how the supervisor selects agents (round-robin, capability-match, load-balance, etc.)
6. **Default implementation** in `Infrastructure.AI/Agents/` — `CapabilityMatchSupervisor` that matches task requirements to agent capabilities + autonomy tier
7. **Integration with `MultiAgentWorkflow`** — Supervisor uses the existing MAF workflow as execution substrate, not a replacement
8. **Escalation callback** — When a subagent hits its autonomy ceiling (tier doesn't allow an action), it escalates back to supervisor, which can either handle it, delegate to a higher-tier agent, or trigger human escalation (Phase 2 dependency)

### Design Constraints

- Supervisor is itself an agent with its own autonomy tier (typically the highest)
- Must not create tight coupling between supervisor and specific agent implementations — use capabilities/profiles, not concrete types
- Delegation records must be immutable and auditable
- Strategy pattern for agent selection enables template consumers to plug in custom logic
- Must work with existing MediatR pipeline (governance checks, audit, etc.)

---

## Cross-Cutting Concerns

### Layer Placement (Clean Architecture)
- **Domain.AI**: `AutonomyLevel`, `AutonomyTierPolicy`, `DelegationState`, `DelegationRecord`, `SupervisorDecisionContext`
- **Application.AI.Common**: `IAutonomyTierResolver`, `ISupervisor`, `ISupervisorStrategy`
- **Application.Core**: MediatR pipeline behavior for autonomy enforcement
- **Infrastructure.AI**: `CapabilityMatchSupervisor`, `DefaultAutonomyTierResolver`, DI registration

### Integration Points
- `IToolPermissionService` — Autonomy tier feeds into permission resolution
- `IGovernancePolicyEngine` — AGT may adjust effective tier at runtime
- `IAuditSink` — Tier changes and delegation events are audit-worthy
- `MultiAgentWorkflow` — Execution substrate for supervised delegation
- `GovernanceApprovalWorkflow` — Escalation path when autonomy is exceeded

### Quality Requirements
- 80% test coverage minimum on all new code
- Full XML documentation on all public types
- Immutable records with init-only properties
- Keyed DI for strategy registrations
- Integration tests verifying permission resolution with autonomy tiers
- Integration tests verifying supervisor delegation lifecycle
