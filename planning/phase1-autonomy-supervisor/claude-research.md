# Research Findings — Phase 1: Autonomy Tiers & Supervisor Agent

## Part 1: Codebase Analysis

### Permission System (Foundation for Autonomy Tiers)

**3-Phase Resolution Algorithm** (`ThreePhasePermissionResolver.cs`):
- Phase 0: Denial rate limiter auto-denies after N denials (`IDenialTracker`)
- Phase 1a: Safety gates checked first — bypass-immune (`ISafetyGateRegistry`)
- Phase 1b: Deny rules evaluated by priority
- Phase 2: Ask rules
- Phase 3: Allow rules (default to Ask if no match)

**Domain Models** (`Domain.AI/Permissions/`):
- `PermissionBehaviorType` — Allow | Deny | Ask
- `PermissionDecision` — Result with reason, matched rule, source
- `PermissionRuleSource` — AgentManifest, SkillDefinition, UserSettings, ProjectSettings, LocalSettings, SessionOverride, PolicySettings, CliArgument (precedence order)
- `ToolPermissionRule` — ToolPattern, OperationPattern, Behavior, Source, Priority, IsBypassImmune
- `SafetyGate` — Bypass-immune gates (`.git/`, `.claude/`, `.ssh/`, `.env`)
- `DenialRecord` — Tracks denial count, first/last denied timestamps

**Pluggable Rule Providers** (`IPermissionRuleProvider`):
- Multiple providers aggregated during resolution
- `ConfigBasedRuleProvider` — currently returns empty (ready for extension)
- Each provider declares a `Source` (PermissionRuleSource) for precedence

**Configuration** (`PermissionsConfig`):
- `DefaultBehavior` (Allow|Ask|Deny), `DenialRateLimitThreshold` (default: 3), `SafetyGatePaths`, `MaxSubcommandLimit`

**MediatR Pipeline** (`ToolPermissionBehavior`):
- Position 6 (after auth, before validation)
- Decision mapping: Allow→proceed, Deny→Forbidden, Ask→PermissionRequired

### Subagent System (Foundation for Supervisor)

**Domain Models** (`Domain.AI/Agents/`):
- `SubagentType` — Explore, Plan, Verify, Execute, General
- `SubagentDefinition` — AgentType, ToolAllowlist/Denylist, PermissionMode, MaxTurns, ModelOverride, SystemPromptOverride, InheritParentTools
- `AgentDefinition` — Id, Name, Description, Category, Domain, Version, Author, Tags, Skill, FilePath
- `AgentExecutionContext` — Runtime: Name, Instruction, DeploymentName, Tools, MiddlewareTypes, AIContextProviders, TraceScope, Temperature, TopP

**Built-in Profiles** (`BuiltInSubagentProfiles.cs`):
| Type | Tools | MaxTurns | PermissionMode | InheritParentTools |
|------|-------|----------|----------------|--------------------|
| Explore | file_system | 10 | Allow | true |
| Plan | (none) | 3 | Allow | false |
| Verify | file_system | 5 | Allow | true |
| Execute | (all) | config | Ask | true |
| General | (all) | 10 | Ask | true |

**Tool Resolution** (`SubagentToolResolver.cs`):
- Allowlist applied first (null = inherit all parent tools)
- Denylist applied second (removes from allowlist)
- InheritParentTools flag enables/disables parent tool inheritance

### MAF Workflow Orchestration

**MultiAgentWorkflow** (`Application.Core/Workflows/Orchestration/`):
- Fan-out/fan-in pattern: runs all agents in parallel on same input, aggregates results
- Uses `WorkflowBuilder` API: `AddFanOutEdge()`, `AddFanInBarrierEdge()`
- `AgentExecutorFactory` creates `ExecutorBinding` from `AgentWorkflowStep` + ChatClient
- `AggregateResultsExecutor` combines outputs

**GovernanceApprovalWorkflow** (`Application.Core/Workflows/Governance/`):
- Static factory `Build(sp)` → (Workflow, RequestPort)
- Graph: CreateApprovalRequest → [RequestPort GovernanceApproval] → ProcessApprovalOutcome
- **RequestPort pattern** for pausable execution waiting on human response
- ApprovalRequest/ApprovalResponse types passed through port

### Governance Pipeline

**Policy Engine** (`IGovernancePolicyEngine`):
- `EvaluateToolCall(agentId, toolName, arguments?)` → GovernanceDecision
- `LoadPolicyFile(yamlPath)` — Runtime YAML policy loading
- **GovernancePolicyAction** — Allow, Deny, Warn, RequireApproval, Log, RateLimit
- **GovernancePolicyScope** — Global, Tenant, Organization, Agent

**AGT Adapter** (`AgtPolicyEngineAdapter.cs`):
- Wraps `AgentGovernance.Policy.PolicyEngine`
- Emits OTel metrics: Decisions, EvaluationDuration, Violations, RateLimitHits

**Audit** (`IGovernanceAuditService`):
- Hash-chain integrity verification (tamper-evident)
- `Log(agentId, action, decision)`, `VerifyChainIntegrity()` → bool

### DI Registration Patterns

**Keyed singletons**: Tools keyed by tool name, Workflows keyed by logical name
**Aggregated providers**: `IEnumerable<IPermissionRuleProvider>` — multiple providers resolved
**Composition order**: ApplicationCommon → ApplicationAI → InfrastructureAI → ApplicationCore → Governance

### Testing Patterns

- **xUnit** + **Moq** + **FluentAssertions**
- Test naming: `Feature_Scenario_Expected` (e.g., `SafetyGate_TakesPrecedence_OverAllRules`)
- Test helpers: `TestableAIAgent` (configurable response/handler), `TestableAgentSession`
- Permission tests: Setup mocks → `CreateResolver(rules)` helper → Act → Assert Decision

---

## Part 2: Industry Best Practices

### Multi-Agent Supervisor Patterns

**Six core orchestration patterns** (industry consensus):

| Pattern | Description | When to Use |
|---------|-------------|-------------|
| Coordinator-Worker | Central coordinator delegates to specialists, aggregates results | Content pipelines, research flows |
| Hierarchical Teams | Nested teams with supervisors managing groups | Enterprise with team leads |
| Sequential Pipeline | Fixed-order processing | Document processing |
| Parallel Fan-Out | Simultaneous execution, synchronized aggregation | Multi-source research |
| Conversation-Based | Iterative dialogue between agents | Code review, debate |
| Blackboard | Shared knowledge base, decentralized | Collaborative analysis |

**Framework landscape** (72% of enterprise AI now uses multi-agent, up from 23% in 2024):
- **LangGraph**: Stateful graphs, conditional routing, interrupt() for HITL — most flexible
- **CrewAI**: Role-based teams, `Process.hierarchical` auto-creates manager — easiest setup
- **MAF/AutoGen**: Event-driven messaging, GroupChat — native .NET

**Resilience patterns**:
- Retries with backoff + circuit breakers per agent
- State checkpointing at every handoff
- Timeout policies on every agent step
- Loop detection (max iteration limits)
- Context summarization between handoffs

### Autonomy Tiers in Enterprise Systems

**Three-tier trust progression** (VisionWrights/industry consensus):

| Tier | Name | Behavior | Gate to Next |
|------|------|----------|-------------|
| 1 | Insight (Restricted) | Read-only, surface info, generate reports | Demonstrate accuracy |
| 2 | Assistive (Supervised) | Recommend + wait for approval | Reliability at Tier 1 |
| 3 | Autonomous | Act within guardrails (limits, scope, escalation) | Sustained track record at Tier 2 |

**Anthropic empirical data** (Feb 2026): Progressive trust — new users auto-approve ~20%, by 750 sessions >40%. Longest sessions doubled (25→45 min). Risk concentrated in security/financial/medical clusters.

**Permission models**:
- **RBAC** (Role-Based) — Simple but rigid
- **ABAC** (Attribute-Based) — Adapts to agent state, user identity, data sensitivity. **Recommended.**
- **IBAC** (Intent-Based) — Evaluates intent, not just action. Aspirational.

**Human-in-the-loop patterns**:
1. Approval gates at defined checkpoints
2. Auto-escalation when confidence < threshold
3. Confidence-based routing (>90% auto, 70-90% optional review, <70% require human)
4. Periodic review checkpoints

### Microsoft Agent Framework Multi-Agent (MAF 1.0)

**Executor/WorkflowBuilder** — Native .NET multi-agent abstraction:
- Typed `Executor<TInput, TOutput>` — compile-time type safety on edges
- `WorkflowBuilder`: `AddEdge()`, `AddFanOutEdge()`, `AddFanInBarrierEdge()`
- Sub-workflows via `BindAsExecutor()` for modular composition
- Middleware pipeline intercepts agent actions (parallels MediatR behaviors)
- `.ConfigureDurableWorkflows()` bridges to Azure Functions for fault tolerance

**Multi-agent patterns** (stable in MAF 1.0): Sequential, Concurrent, Handoff, Group Chat, Magentic-One.

### LLM Agent Governance & Safety

**OWASP Top 10 for LLM Apps 2025**: Prompt injection, sensitive info disclosure, supply chain, data poisoning, improper output handling, **excessive agency (LLM06)**, system prompt leakage, vector/embedding weaknesses, misinformation, unbounded consumption.

**OWASP Top 10 for Agentic Applications** (Dec 2025): Goal hijacking, tool misuse, identity abuse, supply chain, code execution, memory poisoning, insecure communications, cascading failures, human-agent trust exploitation, rogue agents.

**Microsoft AGT** (April 2026) — 7 packages:
- Agent OS: Stateless policy engine (<0.1ms p99), YAML/OPA/Cedar policies
- Agent Mesh: Cryptographic identity (DIDs), dynamic trust scoring (0-1000, 5 tiers)
- Agent Runtime: Execution rings, saga orchestration, kill switch
- MCP Gateway: Governed pipeline evaluating every tool call
- MCP Security Scanner: Detects suspicious tool definitions
- MCP Response Sanitizer: Removes injection, credentials, exfiltration URLs
- Governance Kernel: YAML policy, audit events, OTel metrics

**Zero Trust for Agents** (Microsoft guidance):
- Agents as microservices with isolated permissions
- Explicit action schemas (allowed actions, risk levels, constraints)
- Deterministic HITL through orchestrator logic, not model reasoning
- Least privilege / least action default
- Unique verifiable identity per agent

---

## Key Recommendations for Implementation

### Autonomy Tiers
1. **3-tier model**: Restricted (read-only) → Supervised (recommend+approve) → Autonomous (act within guardrails)
2. Maps to existing `PermissionBehaviorType`: Restricted→Deny default, Supervised→Ask default, Autonomous→Allow default
3. **ABAC-style resolution**: Tier sets baseline, individual `ToolPermissionRule` entries override
4. New `IPermissionRuleProvider` implementation: `AutonomyTierRuleProvider` generates rules from tier definition
5. Tier changes auditable via `IGovernanceAuditService`
6. Configuration via `appsettings.json` for default tier→tool mappings

### Supervisor Agent
1. **Coordinator-Worker pattern** as primary (covers 80%+ enterprise use cases)
2. Build on MAF `WorkflowBuilder` with typed Executors — not a replacement, an extension
3. Strategy pattern for agent selection: `ISupervisorStrategy` (CapabilityMatch, RoundRobin, LoadBalance)
4. Use existing `ISubagentProfileRegistry` for agent discovery + capability matching
5. Delegation state tracking with immutable `DelegationRecord` records
6. Escalation callback when subagent hits autonomy ceiling → supervisor can re-delegate or trigger HITL
7. Loop detection with max delegation depth

### Integration Points
- Autonomy tier feeds into `ThreePhasePermissionResolver` as a new rule provider
- Supervisor uses `AgentExecutorFactory` to create executor bindings for delegation
- Both emit governance metrics via existing OTel infrastructure
- Both integrate with `GovernanceApprovalWorkflow` for human escalation path
