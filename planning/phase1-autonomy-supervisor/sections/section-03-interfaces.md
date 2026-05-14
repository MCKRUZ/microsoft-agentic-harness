# Section 03: Application Interfaces

## Overview

This section defines the application-layer contracts for autonomy tier resolution, supervisor delegation, strategy selection, and delegation persistence. It also modifies two existing types -- `AgentExecutionContext` (Domain) and `AgentExecutionContextFactory` (Application) -- to support delegation context propagation.

These are interfaces and type modifications only. No implementations live here; those come in sections 04-07. Every downstream section (04 through 08) depends on the contracts defined here.

## Prerequisites

- **Section 01** (Domain Autonomy) must be complete. This section references `AutonomyLevel` from `Domain.AI/Governance/AutonomyLevel.cs`.
- **Section 02** (Domain Delegation) must be complete. This section references `DelegationRecord`, `DelegationResult`, `SupervisorDecisionContext`, `AgentCandidate`, `AgentSelection` from `Domain.AI/Orchestration/`.

## Tests

No dedicated tests for this section. Interfaces have no behavior to test; compilation validates correctness. The modifications to `AgentExecutionContext` and `AgentExecutionContextFactory` are tested through integration tests in section 09 and through the implementation tests in sections 06 and 07.

From `claude-plan-tdd.md`:

```
## Section 3: Application Interfaces

No tests — interfaces only. Tested via implementation sections.
```

## Files to Create

### 1. `IAutonomyTierResolver` -- Governance contract

**File:** `src/Content/Application/Application.AI.Common/Interfaces/Governance/IAutonomyTierResolver.cs`

This interface resolves the effective autonomy tier for an agent. It belongs in the `Interfaces/Governance/` folder alongside the existing `IGovernancePolicyEngine`, `IGovernanceAuditService`, etc.

**Key design decisions:**

- **Synchronous.** Tier resolution reads from the in-memory `ISubagentProfileRegistry` -- no I/O, no async needed.
- **Two overloads.** One accepts `SubagentType` (looks up the profile via `ISubagentProfileRegistry`, then reads `AutonomyLevel`). The other accepts `SubagentDefinition` directly (reads `AutonomyLevel` from the definition -- useful when the caller already has the definition in hand).
- **Fallback behavior.** When no profile or definition specifies a tier, falls back to `PermissionsConfig.DefaultAutonomyLevel`. The default implementation (section 04) reads this from `IOptionsMonitor<AppConfig>`.

**Interface signature (stub):**

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

**Namespace:** `Application.AI.Common.Interfaces.Governance`

**Required usings:** `Domain.AI.Agents` (for `SubagentType`, `SubagentDefinition`), `Domain.AI.Governance` (for `AutonomyLevel`)

**Consumed by:**
- `AutonomyTierRuleProvider` (section 04) -- resolves tier during permission rule generation
- `CapabilityMatchSupervisor` (section 07) -- resolves tier when building `AgentCandidate` list

### 2. `ISupervisor` -- Delegation orchestration contract

**File:** `src/Content/Application/Application.AI.Common/Interfaces/Agents/ISupervisor.cs`

This interface coordinates multi-agent task delegation. It belongs in `Interfaces/Agents/` alongside `ISubagentProfileRegistry` and `ISubagentToolResolver`.

**Interface signature (stub):**

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

**Namespace:** `Application.AI.Common.Interfaces.Agents`

**Required usings:** `Domain.AI.Governance` (for `AutonomyLevel`), `Domain.AI.Orchestration` (for `DelegationResult`, `DelegationRecord`)

**Method semantics:**
- `DelegateAsync` -- The main entry point. Builds a decision context, selects an agent via `ISupervisorStrategy`, executes the delegation, persists records, and returns the result. The `toolOverrides` parameter grants additional tools to the selected agent for this delegation only.
- `GetDelegationStatusAsync` -- Reads the latest state for a specific delegation from the store. Returns null if not found.
- `GetActiveDelegationsAsync` -- Returns all delegations in `Pending` or `InProgress` state for the current supervisor session.
- `CancelDelegationAsync` -- Triggers cancellation on a running delegation. Returns `true` if the delegation was found and cancellation was triggered, `false` if the delegation ID is unknown or already completed.

### 3. `ISupervisorStrategy` -- Agent selection contract

**File:** `src/Content/Application/Application.AI.Common/Interfaces/Agents/ISupervisorStrategy.cs`

Pluggable strategy for selecting which agent handles a delegated task. Registered via keyed DI -- the default key is `"capability-match"`.

**Interface signature (stub):**

```csharp
/// <summary>
/// Pluggable strategy for selecting which agent handles a delegated task.
/// Registered via keyed DI — default key is "capability-match".
/// </summary>
public interface ISupervisorStrategy
{
    AgentSelection? SelectAgent(SupervisorDecisionContext context);
}
```

**Namespace:** `Application.AI.Common.Interfaces.Agents`

**Required usings:** `Domain.AI.Orchestration` (for `AgentSelection`, `SupervisorDecisionContext`)

**Key design decisions:**
- **Synchronous.** Selection is a pure function over in-memory data (candidate list, weights, keyword matching). No I/O.
- **Nullable return.** Returns `null` when no candidate survives filtering or all scores fall below the confidence threshold. The supervisor translates this to a delegation failure.
- **Keyed DI.** Multiple strategies can be registered with different keys. The supervisor resolves by key (defaulting to `"capability-match"`). This enables future strategy implementations (e.g., LLM-based selection) without changing the supervisor.

### 4. `IDelegationStore` -- Persistence contract

**File:** `src/Content/Application/Application.AI.Common/Interfaces/Agents/IDelegationStore.cs`

Persists delegation records as append-only JSONL per supervisor session.

**Interface signature (stub):**

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

**Namespace:** `Application.AI.Common.Interfaces.Agents`

**Required usings:** `Domain.AI.Orchestration` (for `DelegationRecord`)

**Method semantics:**
- `AppendAsync` -- Serializes the record as JSON and appends a line to the session file. Thread-safe. Creates directory structure lazily on first write.
- `GetByIdAsync` -- Reads the current session file, filters by `DelegationId`, returns the **latest** record (highest timestamp) for that ID. Multiple records exist per delegation because state transitions are append-only. Returns null if not found. Cross-session lookup is not supported in Phase 1.
- `GetBySessionAsync` -- Reads all lines for the given `supervisorId`, deduplicates by `DelegationId` (keeping the latest state for each).
- `GetByParentAsync` -- Reads all lines, filters by `ParentDelegationId`, deduplicates by `DelegationId`.

## Files to Modify

### 5. `AgentExecutionContext` -- Add delegation properties

**File:** `src/Content/Domain/Domain.AI/Agents/AgentExecutionContext.cs`

Add three typed properties for delegation context propagation. These replace what would otherwise be untyped `AdditionalProperties` entries, providing compile-time safety and discoverability.

**Properties to add:**

```csharp
/// <summary>
/// Current delegation nesting depth. Null when the agent is not executing within a delegation.
/// 0 = top-level delegation, increments with each nested delegation.
/// </summary>
public int? DelegationDepth { get; set; }

/// <summary>
/// Current delegation ID. Links child delegations to their parent.
/// Null when not in a delegation context.
/// </summary>
public Guid? DelegationId { get; set; }

/// <summary>
/// The SubagentType of this agent. Enables tier resolution from agent context
/// without a back-reference to the SubagentDefinition.
/// </summary>
public SubagentType? DelegatingAgentType { get; set; }
```

**Required usings to add:** None new -- `SubagentType` is already in the same namespace (`Domain.AI.Agents`).

**Placement:** Add these properties after the existing `AdditionalProperties` property, before the closing brace. They are optional (nullable) so existing code that creates `AgentExecutionContext` is unaffected.

**Why typed properties instead of `AdditionalProperties`:**
- Compile-time type safety -- no magic string keys
- IntelliSense discoverability
- The `AutonomyTierRuleProvider` (section 04) needs `SubagentType` to resolve the tier, and it should not depend on string key conventions
- `CapabilityMatchSupervisor` (section 07) reads `DelegationDepth` to enforce depth limits -- a nullable int is cleaner than `(int?)context.AdditionalProperties?["DelegationDepth"]`

### 6. `AgentExecutionContextFactory` -- Add delegation factory method

**File:** `src/Content/Application/Application.AI.Common/Factories/AgentExecutionContextFactory.cs`

Add a new public method that creates an `AgentExecutionContext` from a `SubagentDefinition` rather than a `SkillDefinition`. This bridges the supervisor's delegation model to the existing factory infrastructure.

**Method signature (stub):**

```csharp
/// <summary>
/// Creates an execution context for a delegated agent. Used by <see cref="ISupervisor"/>
/// when delegating a task to a subagent. Bypasses skill-based tool resolution,
/// instead using the tools resolved from the subagent definition and any delegation overrides.
/// </summary>
public AgentExecutionContext CreateFromDelegation(
    SubagentDefinition definition,
    IReadOnlyList<string>? toolOverrides,
    int delegationDepth,
    Guid delegationId)
```

**Behavior:**
- Creates a new `AgentExecutionContext` populated from the `SubagentDefinition`:
  - `Name` from `definition.AgentType.ToString() + "Agent"` (e.g., `"ExploreAgent"`)
  - `Instruction` from `definition.SystemPromptOverride` (may be null)
  - `DeploymentName` from `definition.ModelOverride` or config default
  - `DelegationDepth` = the provided depth
  - `DelegationId` = the provided delegation ID
  - `DelegatingAgentType` = `definition.AgentType`
- This method is **synchronous** -- no MCP tool resolution, no skill path resolution. Tools are resolved separately by the supervisor using `ISubagentToolResolver` and passed via the `Tools` property after context creation.
- The `toolOverrides` parameter is stored in `AdditionalProperties["delegationToolOverrides"]` so the supervisor can apply them after tool resolution. Alternatively, the supervisor can resolve tools first and pass them directly -- this is an implementation detail for section 07.

**Why a separate method instead of extending `MapToAgentContextAsync`:**
- `MapToAgentContextAsync` takes a `SkillDefinition` and builds tools from skill declarations, MCP, keyed DI. Delegation bypasses all of this -- tools come from the subagent profile's allowlist/denylist, resolved by `ISubagentToolResolver`.
- Mixing both code paths into one method would add conditional complexity and violate SRP.
- The new method is simpler (synchronous, no skill paths, no MCP) and purpose-built for the delegation flow.

### 7. `PermissionRuleSource` enum -- Add AutonomyTier value

**File:** `src/Content/Domain/Domain.AI/Permissions/PermissionRuleSource.cs`

Add a new enum value to distinguish autonomy-tier-generated rules from other sources in audit logs.

**Value to add:**

```csharp
/// <summary>Rule generated from the agent's autonomy tier assignment.</summary>
AutonomyTier
```

**Placement:** Add after `CliArgument` (or at the end of the enum). The numeric value does not matter for functionality -- `PermissionRuleSource` is used as a tag, not for ordering.

### 8. `SubagentDefinition` -- Add AutonomyLevel property

**File:** `src/Content/Domain/Domain.AI/Agents/SubagentDefinition.cs`

**NOTE:** This modification is defined in section 01 (Domain Autonomy). It is listed here as a dependency reference only. Section 01 adds the `AutonomyLevel` property with a default of `Supervised`. This section's interfaces (`IAutonomyTierResolver`) depend on that property existing.

Do NOT implement this change in section 03 -- it belongs to section 01.

## Dependency Map

```
Section 01 (AutonomyLevel enum, AutonomyTierPolicy, AutonomyExceededResult)
Section 02 (DelegationRecord, DelegationResult, SupervisorDecisionContext, AgentCandidate, AgentSelection, CapabilityScore)
    |
    v
Section 03 (THIS SECTION)
  - IAutonomyTierResolver         --> consumed by Section 04, 07
  - ISupervisor                   --> consumed by Section 07
  - ISupervisorStrategy           --> consumed by Section 05, 07
  - IDelegationStore              --> consumed by Section 06, 07
  - AgentExecutionContext mods    --> consumed by Section 04, 07
  - AgentExecutionContextFactory  --> consumed by Section 07
  - PermissionRuleSource.AutonomyTier --> consumed by Section 04
    |
    v
Section 04 (AutonomyTierRuleProvider implements IPermissionRuleProvider, uses IAutonomyTierResolver)
Section 05 (CapabilityMatchStrategy implements ISupervisorStrategy)
Section 06 (JsonlDelegationStore implements IDelegationStore)
Section 07 (CapabilityMatchSupervisor implements ISupervisor)
```

## Implementation Checklist

1. Create `IAutonomyTierResolver.cs` in `Application.AI.Common/Interfaces/Governance/`
2. Create `ISupervisor.cs` in `Application.AI.Common/Interfaces/Agents/`
3. Create `ISupervisorStrategy.cs` in `Application.AI.Common/Interfaces/Agents/`
4. Create `IDelegationStore.cs` in `Application.AI.Common/Interfaces/Agents/`
5. Add `DelegationDepth`, `DelegationId`, `DelegatingAgentType` properties to `AgentExecutionContext` in `Domain.AI/Agents/AgentExecutionContext.cs`
6. Add `CreateFromDelegation` method to `AgentExecutionContextFactory` in `Application.AI.Common/Factories/AgentExecutionContextFactory.cs`
7. Add `AutonomyTier` value to `PermissionRuleSource` enum in `Domain.AI/Permissions/PermissionRuleSource.cs`
8. Verify the solution builds: `dotnet build src/AgenticHarness.slnx`

## Existing Types Referenced (for context)

These types already exist in the codebase and are referenced by the interfaces defined in this section:

- **`SubagentType`** (`Domain.AI/Agents/SubagentType.cs`) -- enum with `Explore`, `Plan`, `Verify`, `Execute`, `General`
- **`SubagentDefinition`** (`Domain.AI/Agents/SubagentDefinition.cs`) -- record with tool allow/deny lists, permission mode, max turns, model override
- **`ISubagentProfileRegistry`** (`Application.AI.Common/Interfaces/Agents/ISubagentProfileRegistry.cs`) -- maps `SubagentType` to `SubagentDefinition`
- **`ISubagentToolResolver`** (`Application.AI.Common/Interfaces/Agents/ISubagentToolResolver.cs`) -- filters parent tools through subagent allow/deny lists
- **`PermissionBehaviorType`** (`Domain.AI/Permissions/PermissionBehaviorType.cs`) -- enum: `Allow`, `Deny`, `Ask`
- **`PermissionRuleSource`** (`Domain.AI/Permissions/PermissionRuleSource.cs`) -- enum identifying rule origin
- **`ToolPermissionRule`** (`Domain.AI/Permissions/ToolPermissionRule.cs`) -- record with `ToolPattern`, `OperationPattern`, `Behavior`, `Source`, `Priority`, `IsBypassImmune`
- **`IPermissionRuleProvider`** (`Application.AI.Common/Interfaces/Permissions/IPermissionRuleProvider.cs`) -- loads rules for a given agent ID
- **`AgentExecutionContext`** (`Domain.AI/Agents/AgentExecutionContext.cs`) -- runtime execution context passed to Agent Framework (the Domain-layer class, not the scoped Application-layer `IAgentExecutionContext`)
- **`AgentExecutionContextFactory`** (`Application.AI.Common/Factories/AgentExecutionContextFactory.cs`) -- maps skill definitions to runtime agent contexts

## Important Note on Two `AgentExecutionContext` Classes

The codebase has two classes named `AgentExecutionContext`:

1. **`Domain.AI.Agents.AgentExecutionContext`** -- The runtime execution context passed to the Agent Framework. This is the one modified in this section (adding `DelegationDepth`, `DelegationId`, `DelegatingAgentType`). It carries tools, instructions, deployment name, and now delegation metadata.

2. **`Application.AI.Common.Services.Agent.AgentExecutionContext`** -- A scoped ambient context carrying agent identity for the MediatR pipeline. This implements `IAgentExecutionContext` and has `AgentId`, `ConversationId`, `TurnNumber`. This is NOT modified in this section.

The Domain-layer class is what the supervisor populates when delegating; the Application-layer class is what pipeline behaviors read. They serve different purposes and operate at different lifecycle scopes.
