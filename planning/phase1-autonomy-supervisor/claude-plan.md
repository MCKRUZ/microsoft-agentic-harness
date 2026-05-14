# Implementation Plan — Phase 1: Autonomy Tiers & Supervisor Agent

## Context

The Microsoft Agentic Harness is a production-grade Clean Architecture template for building enterprise agent systems on the Microsoft Agent Framework (MAF). It uses CQRS/MediatR, FluentValidation, keyed DI, and a layered architecture (Domain → Application → Infrastructure → Presentation).

The harness currently has a strong permission system (3-phase resolution with pluggable rule providers), a subagent system (typed profiles with tool allow/deny lists), MAF workflow orchestration (fan-out/fan-in, human-in-the-loop via RequestPort), and a governance pipeline (AGT-backed policy engine with audit). What it lacks is the ability to:

1. **Define how much trust an agent has** — currently all agents share the same permission defaults
2. **Coordinate multiple agents on a task** — currently agents execute independently, no delegation or task routing

Phase 1 closes both gaps with Autonomy Tiers and a Supervisor Agent.

---

## Section 1: Domain Models — Autonomy Tiers

### Purpose

Add domain primitives that define the 3-tier trust model. These are value objects with no behavior — they represent the vocabulary of autonomy in the system.

### Types to Create

**`AutonomyLevel` enum** in `Domain.AI/Governance/AutonomyLevel.cs`:
- `Restricted = 0` — Read-only. Default behavior: Ask (forces approval for every action). Safety gates handle true Deny.
- `Supervised = 1` — Recommend-and-wait. Default behavior: Ask. May have specific tool Allow overrides.
- `Autonomous = 2` — Act within guardrails. Default behavior: Allow. Safety gates and AGT policies still apply.

The distinction between Restricted and Supervised is not the generated rule behavior (both generate Ask rules) — it's the tool overrides. Restricted agents have no Allow overrides by default; Supervised agents can have specific tools pre-approved via `ToolOverrides`.

Numeric ordering matters: higher value = more trust. This enables `>=` comparisons for "requires at least tier X" checks.

**`AutonomyTierPolicy` record** in `Domain.AI/Governance/AutonomyTierPolicy.cs`:
- `Level` → AutonomyLevel
- `DefaultBehavior` → PermissionBehaviorType (maps Restricted→Ask, Supervised→Ask, Autonomous→Allow)
- `ToolOverrides` → IReadOnlyDictionary<string, PermissionBehaviorType>? — per-tool behavior overrides within a tier (e.g., Restricted tier still allows `"query_knowledge_graph"`)

**`AutonomyExceededResult` record** in `Domain.AI/Governance/AutonomyExceededResult.cs`:
- `AttemptedAction` → string (tool name or operation)
- `CurrentLevel` → AutonomyLevel
- `RequiredLevel` → AutonomyLevel
- `Reason` → string

### Existing Type Modifications

**`SubagentDefinition`** — Add `AutonomyLevel` property with default value `Supervised`. This is the assignment point: each subagent instance gets a tier.

### Design Rationale

Tiers are orthogonal to `SubagentType`. An Explore agent could be Restricted (read-only browsing) or Autonomous (full filesystem). The tier is per-instance, set when the subagent is defined or the supervisor creates a delegation.

---

## Section 2: Domain Models — Supervisor & Delegation

### Purpose

Add domain primitives for task delegation: tracking who delegated what to whom, the current state, and the result.

### Types to Create

**`DelegationState` enum** in `Domain.AI/Orchestration/DelegationState.cs`:
- `Pending` — Delegation created, not yet started
- `InProgress` — Agent executing
- `Completed` — Successfully finished
- `Failed` — Agent failed (includes autonomy exceeded, timeout, exception)
- `Cancelled` — Explicitly cancelled by supervisor or caller

**`DelegationRecord` record** in `Domain.AI/Orchestration/DelegationRecord.cs`:
- `DelegationId` → Guid
- `ParentDelegationId` → Guid? (null for top-level, set for nested)
- `SupervisorId` → string
- `DelegateAgentId` → string
- `DelegateAgentType` → SubagentType
- `TaskDescription` → string
- `RequiredCapabilities` → IReadOnlyList<string> (tool names needed)
- `ToolOverrides` → IReadOnlyList<string>? (extra tools granted for this delegation)
- `AutonomyLevel` → AutonomyLevel
- `State` → DelegationState
- `DelegationDepth` → int (0 for top-level, increments with nesting)
- `StartedAt` → DateTimeOffset
- `CompletedAt` → DateTimeOffset?
- `FailureReason` → string?
- `AutonomyExceeded` → AutonomyExceededResult? (populated when failure is tier-related)

All properties init-only. State transitions create new records (append-only JSONL pattern).

**`DelegationResult` record** in `Domain.AI/Orchestration/DelegationResult.cs`:
- `IsSuccess` → bool
- `Output` → string?
- `FailureReason` → string?
- `AutonomyExceeded` → AutonomyExceededResult?
- `TokensUsed` → int
- `DurationMs` → long
- Static factories: `Success(output, tokens, duration)`, `Fail(reason)`, `FailAutonomyExceeded(exceeded)`

**`SupervisorDecisionContext` record** in `Domain.AI/Orchestration/SupervisorDecisionContext.cs`:
- `TaskDescription` → string
- `RequiredCapabilities` → IReadOnlyList<string>
- `MinimumAutonomyLevel` → AutonomyLevel
- `AvailableAgents` → IReadOnlyList<AgentCandidate>
- `CurrentDelegationDepth` → int
- `MaxDelegationDepth` → int

**`AgentCandidate` record** in `Domain.AI/Orchestration/AgentCandidate.cs`:
- `AgentId` → string
- `AgentType` → SubagentType
- `AutonomyLevel` → AutonomyLevel
- `AvailableTools` → IReadOnlyList<string>

**`AgentSelection` record** in `Domain.AI/Orchestration/AgentSelection.cs`:
- `SelectedAgent` → AgentCandidate
- `ConfidenceScore` → double (0.0 – 1.0)
- `Reasoning` → string (human-readable explanation for audit)

**`CapabilityScore` record** in `Domain.AI/Orchestration/CapabilityScore.cs`:
- `AgentId` → string
- `ToolCoverage` → double (0.0 – 1.0)
- `TypeAlignment` → double (0.0 – 1.0)
- `TierHeadroom` → double (0.0 – 1.0)
- `TotalScore` → double (weighted composite)

---

## Section 3: Application Interfaces

### Purpose

Define the contracts that the Infrastructure layer implements. These live in `Application.AI.Common/Interfaces/` following existing conventions.

### Interfaces to Create

**`IAutonomyTierResolver`** in `Application.AI.Common/Interfaces/Governance/IAutonomyTierResolver.cs`:
```csharp
/// <summary>
/// Resolves the effective autonomy tier for an agent.
/// Accepts SubagentType because ISubagentProfileRegistry is keyed by type, not agent ID.
/// </summary>
public interface IAutonomyTierResolver
{
    AutonomyLevel Resolve(SubagentType agentType);
    AutonomyLevel Resolve(SubagentDefinition definition);
}
```
Synchronous — reads from in-memory profile registry (no I/O). Two overloads: one looks up the profile by type via `ISubagentProfileRegistry` then reads `AutonomyLevel`; the other reads directly from a `SubagentDefinition`. Falls back to `PermissionsConfig.DefaultAutonomyLevel` when no profile/definition specifies a tier.

Note: The `AutonomyTierRuleProvider` (Section 4) needs to resolve tier during rule aggregation. Since `IPermissionRuleProvider.GetRulesAsync` receives `agentId` (string), the rule provider must also accept `SubagentType` or the tier must be resolvable from the agent ID. The provider will use `ISubagentProfileRegistry.GetProfile(SubagentType)` → `SubagentDefinition.AutonomyLevel`. The agent ID → SubagentType mapping is available from `AgentExecutionContext.AdditionalProperties["SubagentType"]` (set during delegation).

**`ISupervisor`** in `Application.AI.Common/Interfaces/Agents/ISupervisor.cs`:
```csharp
/// <summary>
/// Coordinates multi-agent task delegation using deterministic capability matching.
/// </summary>
public interface ISupervisor
{
    Task<DelegationResult> DelegateAsync(
        string taskDescription,
        IReadOnlyList<string> requiredCapabilities,
        AutonomyLevel minimumTier,
        IReadOnlyList<string>? toolOverrides = null,
        CancellationToken ct = default);

    Task<DelegationRecord?> GetDelegationStatusAsync(Guid delegationId, CancellationToken ct = default);
    Task<IReadOnlyList<DelegationRecord>> GetActiveDelegationsAsync(CancellationToken ct = default);
    Task<bool> CancelDelegationAsync(Guid delegationId, CancellationToken ct = default);
}
```

**`ISupervisorStrategy`** in `Application.AI.Common/Interfaces/Agents/ISupervisorStrategy.cs`:
```csharp
/// <summary>
/// Pluggable strategy for selecting which agent handles a delegated task.
/// Registered via keyed DI — default key is "capability-match".
/// </summary>
public interface ISupervisorStrategy
{
    AgentSelection SelectAgent(SupervisorDecisionContext context);
}
```
No async — selection is a pure function over in-memory data. Fast and testable.

**`IDelegationStore`** in `Application.AI.Common/Interfaces/Agents/IDelegationStore.cs`:
```csharp
/// <summary>
/// Persists delegation records as append-only JSONL per supervisor session.
/// </summary>
public interface IDelegationStore
{
    Task AppendAsync(DelegationRecord record, CancellationToken ct = default);
    Task<DelegationRecord?> GetByIdAsync(Guid delegationId, CancellationToken ct = default);
    Task<IReadOnlyList<DelegationRecord>> GetBySessionAsync(string supervisorId, CancellationToken ct = default);
    Task<IReadOnlyList<DelegationRecord>> GetByParentAsync(Guid parentDelegationId, CancellationToken ct = default);
}
```

---

## Section 4: Autonomy Tier Rule Provider

### Purpose

Bridge the gap between autonomy tiers and the existing permission system. This is the key integration point — a new `IPermissionRuleProvider` that generates baseline permission rules from an agent's tier.

### Location

`Application.Core/Permissions/AutonomyTierRuleProvider.cs` — This is an Application-layer service that depends on `IAutonomyTierResolver` (Application interface) and implements `IPermissionRuleProvider` (Application interface).

### Behavior

1. Called by `ThreePhasePermissionResolver` during rule aggregation (it calls all registered `IPermissionRuleProvider` instances)
2. Resolves the agent's `AutonomyLevel` via `IAutonomyTierResolver`
3. Loads the `AutonomyTierPolicy` for that level (from `IOptionsMonitor<AppConfig>`)
4. Generates rules using the correct phase-aware approach:

**Critical design note:** `ThreePhasePermissionResolver` evaluates rules by behavior type in phases (Phase 1: Deny → Phase 2: Ask → Phase 3: Allow). A Deny rule blocks before Allow rules are ever checked, regardless of priority. Priority only orders rules *within* a phase. Therefore:

- **Restricted**: Generate a global **Ask** rule at `Priority = 0` (catches everything in Phase 2 — forces approval)
- **Supervised**: Generate a global **Ask** rule at `Priority = 0` (same baseline as Restricted)
- **Autonomous**: Generate a global **Allow** rule at `Priority = 0` (catches everything in Phase 3 — auto-approves)

For each entry in `ToolOverrides`:
- Generate an **Allow** rule for the specific tool at `Priority = 10` (allows specific tools even for Restricted/Supervised agents — evaluated in Phase 3 *only if* no Ask rule matches that specific tool first)

**Important:** To make tool overrides work correctly for Restricted/Supervised tiers, the global Ask rule must use a broad pattern (`*`) while override Allow rules use specific tool patterns. The resolver evaluates the *most specific matching rule* within each phase. A specific Allow rule for `"query_knowledge_graph"` will be preferred over the global `"*"` Ask rule because `ThreePhasePermissionResolver` sorts by priority within each phase — the override at Priority 10 beats the global at Priority 0.

5. Returns the generated rules with `Source = PermissionRuleSource.AutonomyTier` (new enum value — distinguishes from AGT PolicySettings rules in audit logs)

### Why This Approach

The tier rule provider plugs into the existing architecture — no changes to `ThreePhasePermissionResolver` itself. It's just another rule source, evaluated alongside agent manifest rules, skill definitions, session overrides, etc. Higher-priority rules from other sources (manifest, session) take precedence over tier defaults.

### Existing Type Modifications

**`PermissionRuleSource` enum** — Add `AutonomyTier` value. Enables audit log filtering to distinguish tier-generated rules from other PolicySettings rules.

---

## Section 5: Capability Match Strategy

### Purpose

The deterministic algorithm that selects which agent handles a delegated task. This is the supervisor's brain — it must be fast, predictable, and auditable.

### Location

`Infrastructure.AI/Agents/CapabilityMatchStrategy.cs`

### Algorithm

**Phase 1 — Filter:**
1. Remove agents whose `AutonomyLevel < minimumTier`
2. Remove agents that lack ALL required tools (even after applying delegation tool overrides)

**Phase 2 — Score (for each remaining candidate):**

| Factor | Weight | Calculation |
|--------|--------|-------------|
| Tool Coverage | 0.4 | `requiredTools.Intersect(agentTools).Count / requiredTools.Count` |
| Type Alignment | 0.3 | 1.0 if `SubagentType` matches task category, 0.5 for General, 0.0 for mismatch |
| Tier Headroom | 0.3 | `(agentTier - minimumTier + 1) / (MaxTierValue + 1)` where `MaxTierValue = 2` (defined as a domain constant, not `Enum.MaxValue`) |

Note: `HistoricalSuccessRate` is deferred to Phase 3 (Learnings Log). Without an aggregation infrastructure, it would always be null/0.5 — dead weight. The 0.1 weight is redistributed to Tier Headroom (0.2 → 0.3).

Weights are configurable via `SubagentConfig.CapabilityMatchWeights`. Weights are **normalized at construction time** — if configured values don't sum to 1.0, the constructor divides each by the total. This prevents misconfiguration from producing scores > 1.0.

**Phase 3 — Select:**
1. Sort by `TotalScore` descending
2. If tied, prefer lower tier (least privilege principle)
3. If still tied, prefer agent type that most closely matches (Explore for research tasks, Execute for action tasks)
4. Return `AgentSelection` with winner, score, and reasoning string

**Edge cases:**
- No candidates after filtering → return null (supervisor reports failure)
- Single candidate → skip scoring, select directly
- All scores below configurable threshold → return null (no confident match)

### Task-to-Type Mapping

The strategy needs to map task descriptions to `SubagentType` for the Type Alignment factor. This uses a simple keyword-based classifier (not LLM):

| Keywords | Maps To |
|----------|---------|
| search, find, read, explore, analyze | Explore |
| plan, design, architect, structure | Plan |
| test, verify, check, validate | Verify |
| execute, run, build, create, write, modify | Execute |
| (none match) | General |

**Tie-breaking:** When a task description matches keywords from multiple categories (e.g., "search and create"), count matches per category. Most matches wins. If still tied, prefer Execute (bias toward action — the agent can always read as part of execution).

This is a heuristic, not a classifier — the 0.3 weight ensures it influences but doesn't dominate. The tool coverage factor (0.4 weight) is the primary signal.

---

## Section 6: Supervisor Implementation

### Purpose

The core supervisor that ties together strategy selection, delegation execution, state tracking, and depth management.

### Location

`Infrastructure.AI/Agents/CapabilityMatchSupervisor.cs`

### Dependencies

- `ISupervisorStrategy` (keyed: `"capability-match"`) — agent selection
- `IDelegationStore` — persistence
- `ISubagentProfileRegistry` — available agent profiles
- `ISubagentToolResolver` — tool resolution with overrides
- `IAutonomyTierResolver` — tier lookup
- `IGovernanceAuditService` — audit trail
- `IOptionsMonitor<AppConfig>` — configuration (MaxDelegationDepth, timeouts)
- `ILogger<CapabilityMatchSupervisor>` — structured logging

### Execution Flow

```
DelegateAsync(task, capabilities, minTier, toolOverrides):
  1. Build SupervisorDecisionContext:
     - Enumerate available agents from ISubagentProfileRegistry
     - For each, resolve tools (ISubagentToolResolver with overrides applied)
     - For each, resolve AutonomyLevel (IAutonomyTierResolver)
     - Set CurrentDelegationDepth from call context
  2. Check depth: if CurrentDelegationDepth >= MaxDelegationDepth → fail with depth exceeded
  3. Call ISupervisorStrategy.SelectAgent(context)
     - If no agent selected → fail with NoCapableAgent
  4. Create DelegationRecord (State=Pending), append to IDelegationStore
  5. Audit: log delegation decision (agent selected, score, reasoning)
  6. Execute agent:
     - Build AgentExecutionContext via AgentExecutionContextFactory
     - Apply tool overrides to the resolved tool set
     - Set delegation depth = current + 1 in agent context metadata
     - Run with CancellationToken linked to DelegationTimeoutSeconds
  7. On completion:
     - Success: Create updated record (State=Completed), append to store
     - Failure: Create updated record (State=Failed, FailureReason), append to store
     - AutonomyExceeded: Populate AutonomyExceededResult in failure record
  8. Audit: log delegation outcome
  9. Return DelegationResult
```

### Existing Type Modifications

**`AgentExecutionContext`** — Add typed properties instead of using untyped `AdditionalProperties`:
- `int? DelegationDepth` — current nesting depth (null = not in a delegation)
- `Guid? DelegationId` — current delegation ID (for linking child delegations)
- `SubagentType? DelegatingAgentType` — the SubagentType, enabling tier resolution from agent context

**`AgentExecutionContextFactory`** — Add a new factory method overload:
```csharp
AgentExecutionContext CreateFromDelegation(SubagentDefinition definition, IReadOnlyList<string>? toolOverrides, int delegationDepth, Guid delegationId);
```
This bridges from the supervisor's delegation abstractions to the factory's existing context creation, without requiring a `SkillDefinition`.

### Multi-Level Delegation

When an agent within a delegation itself calls `ISupervisor.DelegateAsync`:
- The `CurrentDelegationDepth` is read from `AgentExecutionContext.DelegationDepth` (typed property, not magic string)
- `ParentDelegationId` links child to parent delegation via `AgentExecutionContext.DelegationId`
- At `MaxDelegationDepth`, further delegation attempts fail with a structured error

### Concurrency & Cancellation

**Concurrency limiting:** A `SemaphoreSlim(MaxConcurrentDelegations)` at the top of `DelegateAsync` enforces the configured limit. Callers block (with timeout) if at capacity.

**Cancellation:** Active delegations store their `CancellationTokenSource` in a `ConcurrentDictionary<Guid, CancellationTokenSource>`. `CancelDelegationAsync(delegationId)` looks up and triggers the source. The linked token propagates cancellation to the running agent. Sources are removed from the dictionary on delegation completion (success, failure, or cancellation).

---

## Section 7: JSONL Delegation Store

### Purpose

Persist delegation records as append-only JSONL files. One file per supervisor session, consistent with the existing `JsonlAgentHistoryStore` pattern.

### Location

`Infrastructure.AI/Agents/JsonlDelegationStore.cs`

### File Layout

```
{DelegationStoragePath}/
  {supervisorId}/
    {sessionTimestamp}.jsonl
```

Each line is a JSON-serialized `DelegationRecord`. State transitions append new lines (records are immutable — a completed delegation has both the Pending and Completed records in the file).

### Operations

- `AppendAsync` — Serialize record as JSON, append line to session file. Thread-safe via `SemaphoreSlim(1, 1)` per file.
- `GetByIdAsync` — Read all lines from the **current session file only**, deserialize, filter by DelegationId, **return the latest record** (highest timestamp) for that ID. Multiple records exist per delegation due to append-only state transitions. Cross-session lookup is not supported in Phase 1 — document this limitation.
- `GetBySessionAsync` — Read all lines, deduplicate by DelegationId (keep latest state per delegation).
- `GetByParentAsync` — Read all lines, filter by ParentDelegationId, deduplicate by DelegationId.

### Concurrency

Uses `SemaphoreSlim(1, 1)` per file path, stored in a **bounded LRU `ConcurrentDictionary`** with a max of 100 entries (avoids unbounded memory growth across many supervisor sessions). When capacity is reached, evict least-recently-used entries and dispose their semaphores.

Reads also acquire the semaphore — on Windows, `File.AppendAllTextAsync` is not atomic and a concurrent read can observe partially-written lines. Reads must catch `JsonException` for corrupted/partial lines and skip them, consistent with the existing `JsonlAgentHistoryStore` pattern.

### Configuration

- `DelegationStoragePath` from `SubagentConfig` (default: `.agent-sessions/delegations`)
- Files created lazily on first append

---

## Section 8: DI Registration & Configuration

### Purpose

Wire everything together. Register new services, add configuration bindings, and ensure the existing pipeline behaviors work with autonomy tiers.

### Infrastructure.AI/DependencyInjection.cs Additions

```csharp
// Autonomy
services.AddSingleton<IAutonomyTierResolver, DefaultAutonomyTierResolver>();

// Supervisor
services.AddSingleton<IDelegationStore, JsonlDelegationStore>();
services.AddKeyedSingleton<ISupervisorStrategy>("capability-match", (sp, _) =>
    new CapabilityMatchStrategy(sp.GetRequiredService<IOptionsMonitor<AppConfig>>()));
services.AddSingleton<ISupervisor, CapabilityMatchSupervisor>();
```

### Application.Core/DependencyInjection.cs Additions

```csharp
// Register AutonomyTierRuleProvider as an additional IPermissionRuleProvider
services.AddSingleton<IPermissionRuleProvider, AutonomyTierRuleProvider>();
```

This is all that's needed — `ThreePhasePermissionResolver` already aggregates all `IPermissionRuleProvider` instances via `IEnumerable<IPermissionRuleProvider>`.

### Configuration Schema Additions

**New Config POCO** — `AutonomyTierPolicyConfig` in `Domain.Common/Config/AI/Permissions/AutonomyTierPolicyConfig.cs`:
- `DefaultBehavior` → string (parsed to PermissionBehaviorType)
- `ToolOverrides` → Dictionary<string, string>? (tool name → behavior)

**PermissionsConfig** — Add:
- `DefaultAutonomyLevel` (string, default: "Supervised")
- `TierPolicies` → Dictionary<string, AutonomyTierPolicyConfig>

**New Config POCO** — `CapabilityMatchWeightsConfig` in `Domain.Common/Config/AI/Orchestration/CapabilityMatchWeightsConfig.cs`:
- `ToolCoverage` → double (default: 0.4)
- `TypeAlignment` → double (default: 0.3)
- `TierHeadroom` → double (default: 0.3)

**SubagentConfig** — Add:
- `MaxDelegationDepth` (int, default: 3)
- `DelegationStoragePath` (string, default: ".agent-sessions/delegations")
- `DelegationTimeoutSeconds` (int, default: 300)
- `MaxConcurrentDelegations` (int, default: 5)
- `CapabilityMatchWeights` → CapabilityMatchWeightsConfig

### Example appsettings.json

```json
{
  "AI": {
    "Permissions": {
      "DefaultAutonomyLevel": "Supervised",
      "TierPolicies": {
        "Restricted": {
          "DefaultBehavior": "Ask",
          "ToolOverrides": {
            "query_knowledge_graph": "Allow"
          }
        },
        "Supervised": {
          "DefaultBehavior": "Ask",
          "ToolOverrides": {
            "query_knowledge_graph": "Allow",
            "file_system_read": "Allow"
          }
        },
        "Autonomous": {
          "DefaultBehavior": "Allow"
        }
      }
    },
    "Orchestration": {
      "MaxDelegationDepth": 3,
      "DelegationStoragePath": ".agent-sessions/delegations",
      "DelegationTimeoutSeconds": 300,
      "MaxConcurrentDelegations": 5,
      "CapabilityMatchWeights": {
        "ToolCoverage": 0.4,
        "TypeAlignment": 0.3,
        "TierHeadroom": 0.3
      }
    }
  }
}
```

### OTel Metrics

Add to existing `GovernanceMetrics` (or new `SupervisorMetrics`):
- `harness.delegations.total` (counter) — tags: supervisor_id, delegate_agent_id, outcome
- `harness.delegations.duration_ms` (histogram) — delegation execution time
- `harness.autonomy.exceeded_total` (counter) — tags: agent_id, attempted_action, current_tier
- `harness.supervisor.selection_score` (histogram) — capability match scores for observability

---

## Section 9: Test Plan

### Unit Tests

**Autonomy Tier Tests** (`Infrastructure.AI.Tests/Governance/`):
- `AutonomyTierRuleProvider` generates correct rules for each tier level
- `AutonomyTierRuleProvider` generates tool overrides at correct priority
- `DefaultAutonomyTierResolver` reads from SubagentDefinition
- `DefaultAutonomyTierResolver` falls back to config default when no profile exists

**Permission Integration Tests** (`Infrastructure.AI.Tests/Permissions/`):
- `ThreePhasePermissionResolver` with autonomy tier rules — Restricted tier generates Ask (not Deny) for all tools
- `ThreePhasePermissionResolver` with autonomy tier rules — Autonomous tier generates Allow baseline
- `ThreePhasePermissionResolver` with autonomy tier rules — higher-priority manifest Deny rule overrides Autonomous Allow
- `ThreePhasePermissionResolver` with autonomy tier rules — tool override Allow works for Restricted agent on specific tool
- `ThreePhasePermissionResolver` with autonomy tier rules — tool override coexists with global Ask rule

**Capability Match Tests** (`Infrastructure.AI.Tests/Agents/`):
- `CapabilityMatchStrategy` filters agents below minimum tier
- `CapabilityMatchStrategy` filters agents lacking required tools
- `CapabilityMatchStrategy` scores tool coverage correctly
- `CapabilityMatchStrategy` prefers lower tier on tie (least privilege)
- `CapabilityMatchStrategy` returns null when no candidates survive filtering
- `CapabilityMatchStrategy` handles single candidate (skip scoring)
- Task keyword classifier maps correctly to SubagentType

**Supervisor Tests** (`Infrastructure.AI.Tests/Agents/`):
- `CapabilityMatchSupervisor` delegates successfully and returns result
- `CapabilityMatchSupervisor` fails when delegation depth exceeded
- `CapabilityMatchSupervisor` fails when no capable agent found
- `CapabilityMatchSupervisor` records delegation to store on success
- `CapabilityMatchSupervisor` records failure to store on failure
- `CapabilityMatchSupervisor` propagates autonomy exceeded result
- `CapabilityMatchSupervisor` applies tool overrides to selected agent
- `CapabilityMatchSupervisor` emits audit events
- `CapabilityMatchSupervisor` CancelDelegationAsync propagates cancellation to running agent
- `CapabilityMatchSupervisor` enforces MaxConcurrentDelegations via semaphore

**Delegation Store Tests** (`Infrastructure.AI.Tests/Agents/`):
- `JsonlDelegationStore` append and read round-trip
- `JsonlDelegationStore` GetByIdAsync returns latest state for delegation with multiple records
- `JsonlDelegationStore` query by parent ID (nested delegations)
- `JsonlDelegationStore` concurrent appends don't corrupt file
- `JsonlDelegationStore` creates directory structure lazily
- `JsonlDelegationStore` handles partial/corrupted JSON lines gracefully (skip without crash)
- `JsonlDelegationStore` GetBySessionAsync deduplicates by DelegationId

### Integration Tests

- Full pipeline: Restricted agent attempts tool → ToolPermissionBehavior returns PermissionRequired (Ask behavior)
- Full pipeline: Supervised agent attempts tool → ToolPermissionBehavior returns PermissionRequired
- Supervisor delegates to agent, agent hits autonomy ceiling, supervisor receives structured failure
- Multi-level delegation: supervisor → agent → sub-agent, depth limit enforced

---

## Implementation Order

The recommended build sequence (each section is independently implementable after its dependencies):

1. **Section 1** (Domain: Autonomy) — No dependencies. Pure value objects.
2. **Section 2** (Domain: Delegation) — No dependencies. Pure value objects.
3. **Section 3** (Interfaces) — Depends on Sections 1-2 (references domain types).
4. **Section 4** (Tier Rule Provider) — Depends on Section 3 (implements IPermissionRuleProvider, uses IAutonomyTierResolver).
5. **Section 7** (JSONL Store) — Depends on Section 3 (implements IDelegationStore).
6. **Section 5** (Capability Strategy) — Depends on Section 3 (implements ISupervisorStrategy).
7. **Section 6** (Supervisor) — Depends on Sections 3-7 (uses all interfaces and implementations).
8. **Section 8** (DI & Config) — Depends on all sections (wires everything).
9. **Section 9** (Tests) — Can be written in parallel with each section (TDD approach).

Sections 1-2 can be built in parallel. Sections 4, 5, 7 can be built in parallel (all depend only on Section 3). Section 6 is the critical path — it needs everything else.
