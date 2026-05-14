# Section 1: Domain Models — Autonomy Tiers

## Overview

This section adds the domain primitives that define the 3-tier trust model for agents. These are pure value objects (enums and records) with no behavior or external dependencies. They live in the Domain layer and represent the vocabulary of autonomy in the system.

There are no dependencies on other sections. Sections 03 (Interfaces) and 04 (Tier Rule Provider) depend on the types created here.

---

## Tests

No unit tests are needed for this section. The types are pure enums and records with no behavior — compilation validates correctness. Tests for the types that *use* these primitives live in later sections (Section 04 for `AutonomyTierRuleProvider`, Section 09 for integration tests).

---

## Background: Existing Types

The following existing types are referenced or modified in this section. You need to understand their shape but not their full implementation.

**`PermissionBehaviorType` enum** at `src/Content/Domain/Domain.AI/Permissions/PermissionBehaviorType.cs`:
- `Allow` — tool use permitted without confirmation
- `Deny` — tool use blocked
- `Ask` — tool use requires explicit user/caller confirmation

**`PermissionRuleSource` enum** at `src/Content/Domain/Domain.AI/Permissions/PermissionRuleSource.cs`:
- Existing values: `AgentManifest`, `SkillDefinition`, `UserSettings`, `ProjectSettings`, `LocalSettings`, `SessionOverride`, `PolicySettings`, `CliArgument`
- This section adds a new value (see below)

**`SubagentDefinition` record** at `src/Content/Domain/Domain.AI/Agents/SubagentDefinition.cs`:
- Sealed record with properties: `AgentType` (SubagentType), `ToolAllowlist`, `ToolDenylist`, `PermissionMode`, `MaxTurns`, `ModelOverride`, `SystemPromptOverride`, `InheritParentTools`
- Uses `Domain.AI.Permissions` namespace
- This section adds a new property (see below)

**`SubagentType` enum** at `src/Content/Domain/Domain.AI/Agents/SubagentType.cs`:
- Values: `Explore`, `Plan`, `Verify`, `Execute`, `General`

---

## New Files to Create

### 1. `AutonomyLevel` enum

**File:** `src/Content/Domain/Domain.AI/Governance/AutonomyLevel.cs`

**Namespace:** `Domain.AI.Governance`

Create an enum with three members representing trust tiers. Numeric ordering matters — higher value equals more trust, enabling `>=` comparisons for "requires at least tier X" checks.

Members:
- `Restricted = 0` — Read-only. Default permission behavior is Ask (forces approval for every action). Safety gates handle true Deny scenarios.
- `Supervised = 1` — Recommend-and-wait. Default behavior is also Ask. Differs from Restricted in that Supervised agents can have specific tool Allow overrides via `ToolOverrides` in config.
- `Autonomous = 2` — Act within guardrails. Default behavior is Allow. Safety gates and AGT policies still apply as a ceiling.

Include full XML documentation on the enum and each member. This is a template — the docs are teaching material for consumers.

**Design note:** Tiers are orthogonal to `SubagentType`. An Explore agent could be Restricted (read-only browsing) or Autonomous (full filesystem). The tier is per-instance, set when the subagent is defined or the supervisor creates a delegation.

### 2. `AutonomyTierPolicy` record

**File:** `src/Content/Domain/Domain.AI/Governance/AutonomyTierPolicy.cs`

**Namespace:** `Domain.AI.Governance`

Create an immutable record that maps an autonomy level to its permission behavior and per-tool overrides. This is a value object — no behavior, just structured data.

Properties (all init-only):
- `Level` — `AutonomyLevel` — which tier this policy applies to
- `DefaultBehavior` — `PermissionBehaviorType` — the baseline permission behavior for this tier (Restricted/Supervised map to Ask, Autonomous maps to Allow)
- `ToolOverrides` — `IReadOnlyDictionary<string, PermissionBehaviorType>?` — per-tool behavior overrides within the tier (e.g., a Restricted agent might still Allow `"query_knowledge_graph"`)

The record must reference both `Domain.AI.Governance` (for `AutonomyLevel`) and `Domain.AI.Permissions` (for `PermissionBehaviorType`).

### 3. `AutonomyExceededResult` record

**File:** `src/Content/Domain/Domain.AI/Governance/AutonomyExceededResult.cs`

**Namespace:** `Domain.AI.Governance`

Create an immutable record that captures the structured details when an agent attempts an operation above its trust tier. This record is embedded in `DelegationResult` (Section 02) and `DelegationRecord` (Section 02) to provide actionable failure information to the supervisor.

Properties (all init-only):
- `AttemptedAction` — `string` — the tool name or operation the agent tried to invoke
- `CurrentLevel` — `AutonomyLevel` — the agent's current autonomy tier
- `RequiredLevel` — `AutonomyLevel` — the minimum tier needed for the attempted action
- `Reason` — `string` — human-readable explanation of why the action was blocked

---

## Existing Files to Modify

### 4. Add `AutonomyLevel` property to `SubagentDefinition`

**File:** `src/Content/Domain/Domain.AI/Agents/SubagentDefinition.cs`

Add a new property to the existing `SubagentDefinition` sealed record:

- `AutonomyLevel` — `AutonomyLevel` — the trust tier assigned to this subagent instance, with a default value of `AutonomyLevel.Supervised`

This is the assignment point: each subagent instance gets a tier when defined. The default of `Supervised` means existing subagent definitions continue to work without modification (backwards-compatible).

Add a `using Domain.AI.Governance;` directive to the file.

Include XML documentation explaining that this property controls the agent's baseline permission behavior and that tiers are orthogonal to `SubagentType`.

### 5. Add `AutonomyTier` to `PermissionRuleSource` enum

**File:** `src/Content/Domain/Domain.AI/Permissions/PermissionRuleSource.cs`

Add a new enum value:

- `AutonomyTier` — rules generated from an agent's autonomy tier assignment

This value enables audit log filtering to distinguish tier-generated permission rules from other rule sources (AgentManifest, PolicySettings, etc.). The `AutonomyTierRuleProvider` (Section 04) stamps its generated rules with this source.

Add XML documentation on the new member.

---

## File Summary

| Action | File Path |
|--------|-----------|
| Create | `src/Content/Domain/Domain.AI/Governance/AutonomyLevel.cs` |
| Create | `src/Content/Domain/Domain.AI/Governance/AutonomyTierPolicy.cs` |
| Create | `src/Content/Domain/Domain.AI/Governance/AutonomyExceededResult.cs` |
| Modify | `src/Content/Domain/Domain.AI/Agents/SubagentDefinition.cs` |
| Modify | `src/Content/Domain/Domain.AI/Permissions/PermissionRuleSource.cs` |

---

## Verification

After implementation, run:

```
dotnet build src/AgenticHarness.slnx
```

All types are pure domain primitives with no behavior. If the solution builds, the section is complete. No runtime tests are required for this section — the downstream sections (04, 05, 06, 07) exercise these types through their implementations.

---

## Dependencies on Other Sections

- **None.** This section has zero dependencies and can be implemented first.
- **Blocks:** Section 03 (Interfaces) references `AutonomyLevel` and `AutonomyExceededResult`. Section 04 (Tier Rule Provider) uses `AutonomyTierPolicy` and `AutonomyLevel`. Section 02 (Domain: Delegation) references `AutonomyLevel` and `AutonomyExceededResult`.
