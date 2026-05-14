# Section 4: Autonomy Tier Rule Provider

## Overview

This section implements two components that bridge the autonomy tier system into the existing permission pipeline:

1. **`AutonomyTierRuleProvider`** -- an `IPermissionRuleProvider` that generates baseline permission rules from an agent's autonomy tier
2. **`DefaultAutonomyTierResolver`** -- the default `IAutonomyTierResolver` that reads tier assignments from `SubagentDefinition` or falls back to configuration

The tier rule provider is the key integration point. It translates "how much trust does this agent have?" into concrete `ToolPermissionRule` objects that `ThreePhasePermissionResolver` already knows how to evaluate -- no changes to the resolver itself.

---

## Dependencies on Other Sections

**Section 01 (Domain Autonomy)** must be complete. This section uses:
- `AutonomyLevel` enum from `Domain.AI/Governance/AutonomyLevel.cs`
- `AutonomyTierPolicy` record from `Domain.AI/Governance/AutonomyTierPolicy.cs`
- The `AutonomyTier` value on the `PermissionRuleSource` enum
- The `AutonomyLevel` property on `SubagentDefinition`

**Section 03 (Interfaces)** must be complete. This section implements:
- `IAutonomyTierResolver` from `Application.AI.Common/Interfaces/Governance/IAutonomyTierResolver.cs`

---

## Existing Code Context

The implementer needs to understand these existing types and their relationships:

**`IPermissionRuleProvider`** (`Application.AI.Common/Interfaces/Permissions/IPermissionRuleProvider.cs`):
```csharp
public interface IPermissionRuleProvider
{
    PermissionRuleSource Source { get; }
    Task<IReadOnlyList<ToolPermissionRule>> GetRulesAsync(string agentId, CancellationToken cancellationToken = default);
}
```

**`ToolPermissionRule`** (`Domain.AI/Permissions/ToolPermissionRule.cs`):
```csharp
public sealed record ToolPermissionRule(
    string ToolPattern,
    string? OperationPattern,
    PermissionBehaviorType Behavior,
    PermissionRuleSource Source,
    int Priority,
    bool IsBypassImmune = false);
```

**`PermissionBehaviorType`** enum: `Allow`, `Deny`, `Ask`

**`PermissionRuleSource`** enum -- Section 01 adds an `AutonomyTier` value to this existing enum.

**`ThreePhasePermissionResolver`** (`Infrastructure.AI/Permissions/ThreePhasePermissionResolver.cs`):
- Collects rules from ALL registered `IPermissionRuleProvider` instances via `IEnumerable<IPermissionRuleProvider>`
- Sorts all rules by `Priority` ascending (lower number = checked first)
- Evaluates in phases: Phase 1 (Deny) -> Phase 2 (Ask) -> Phase 3 (Allow)
- Within each phase, returns the **first matching** rule of that behavior type
- If no rule matches in any phase, defaults to Ask

**`ISubagentProfileRegistry`** (`Application.AI.Common/Interfaces/Agents/ISubagentProfileRegistry.cs`):
```csharp
public interface ISubagentProfileRegistry
{
    SubagentDefinition GetProfile(SubagentType type);
    IReadOnlyDictionary<SubagentType, SubagentDefinition> GetAllProfiles();
}
```

**`ConfigBasedRuleProvider`** (`Infrastructure.AI/Permissions/ConfigBasedRuleProvider.cs`) -- existing reference implementation of `IPermissionRuleProvider`. Currently returns empty rules. Good pattern to follow for constructor shape and DI usage.

**`IAutonomyTierResolver`** (from Section 03):
```csharp
public interface IAutonomyTierResolver
{
    AutonomyLevel Resolve(SubagentType agentType);
    AutonomyLevel Resolve(SubagentDefinition definition);
}
```
Synchronous. Reads from in-memory profile registry (no I/O).

---

## Critical Design Detail: How Tool Overrides Interact with 3-Phase Resolution

The plan states that tool-specific Allow overrides at Priority 10 will be "preferred over" the global Ask rule at Priority 0. **This is incorrect given the actual resolver implementation.** Here is what actually happens:

1. `ThreePhasePermissionResolver.FindFirstMatchingRule` filters rules by `Behavior` type, then returns the first match (lowest priority number) within that behavior type.
2. Phase 2 (Ask) runs before Phase 3 (Allow). A global `"*"` Ask rule at Priority 0 will match ANY tool in Phase 2. Phase 3 (Allow) never executes for that tool.
3. Therefore, an Allow override at Priority 10 for `"query_knowledge_graph"` will never be reached if a `"*"` Ask rule exists.

**The resolver does not support cross-phase specificity-based precedence.** Priority only orders rules within a single phase.

**Design decision for this section:** Generate both the global baseline rule and tool-specific Allow overrides as documented. The tool overrides serve two purposes:
1. **Audit trail** -- the generated Allow rules appear in the rule set, making the agent's intended permissions visible in logs and governance audits even if the resolver doesn't reach them
2. **Future compatibility** -- when the resolver gains specificity-based cross-phase precedence (planned enhancement), the overrides will work without regenerating rules

For Phase 1, the practical effect is: Restricted and Supervised agents get Ask for all tools (including overridden ones). Autonomous agents get Allow for all tools. The ToolOverrides in config are metadata, not enforceable via the current resolver. Document this limitation in the XML docs.

If the implementer or reviewer wants overrides to be enforceable immediately, the alternative is to not generate a global `"*"` Ask rule when overrides exist, and instead enumerate the agent's tool allowlist to generate individual Ask rules for non-overridden tools only. This requires `ISubagentToolResolver` or the tool allowlist from `SubagentDefinition`. This is a valid enhancement but adds complexity. The section as specified uses the simpler approach.

---

## Tests FIRST

### `AutonomyTierRuleProviderTests` 

**File:** `src/Content/Tests/Infrastructure.AI.Tests/Governance/AutonomyTierRuleProviderTests.cs`

Note: despite `AutonomyTierRuleProvider` living in Application.Core, its tests live in Infrastructure.AI.Tests because that project already has the test infrastructure and references needed for permission system tests.

```csharp
// Test: GetRulesAsync_RestrictedTier_GeneratesGlobalAskRule
//   Arrange: Mock IAutonomyTierResolver returning Restricted
//            Config with Restricted tier policy (DefaultBehavior = "Ask")
//   Act: Call GetRulesAsync("restricted-agent")
//   Assert: Returns exactly one rule (plus any override rules)
//           Rule has: ToolPattern = "*", OperationPattern = null,
//                     Behavior = Ask, Source = AutonomyTier, Priority = 0

// Test: GetRulesAsync_SupervisedTier_GeneratesGlobalAskRule
//   Arrange: Mock IAutonomyTierResolver returning Supervised
//            Config with Supervised tier policy (DefaultBehavior = "Ask")
//   Act: Call GetRulesAsync("supervised-agent")
//   Assert: Returns Ask rule with pattern "*", Priority 0, Source AutonomyTier
//           Same baseline behavior as Restricted

// Test: GetRulesAsync_AutonomousTier_GeneratesGlobalAllowRule
//   Arrange: Mock IAutonomyTierResolver returning Autonomous
//            Config with Autonomous tier policy (DefaultBehavior = "Allow")
//   Act: Call GetRulesAsync("autonomous-agent")
//   Assert: Returns Allow rule with pattern "*", Priority 0

// Test: GetRulesAsync_WithToolOverrides_GeneratesOverrideRulesAtHigherPriority
//   Arrange: Config with Restricted tier + ToolOverrides { "query_kg": "Allow" }
//   Act: Call GetRulesAsync for a Restricted agent
//   Assert: Returns 2 rules:
//           - Global Ask rule at Priority 0 with pattern "*"
//           - Specific Allow rule for "query_kg" at Priority 10
//           Both have Source = AutonomyTier

// Test: GetRulesAsync_NoTierPolicy_UsesDefaultBehavior
//   Arrange: Config has no TierPolicies dictionary at all, or no entry for the resolved tier
//   Act: Call GetRulesAsync
//   Assert: Falls back to generating rules from PermissionsConfig.DefaultBehavior
//           (defaults to "Ask" per existing config)

// Test: Source_ReturnsAutonomyTier
//   Assert: provider.Source == PermissionRuleSource.AutonomyTier
```

### `DefaultAutonomyTierResolverTests`

**File:** `src/Content/Tests/Infrastructure.AI.Tests/Governance/DefaultAutonomyTierResolverTests.cs`

```csharp
// Test: Resolve_KnownSubagentType_ReturnsDefinitionAutonomyLevel
//   Arrange: ISubagentProfileRegistry returns SubagentDefinition with AutonomyLevel = Restricted
//   Act: resolver.Resolve(SubagentType.Explore)
//   Assert: Returns AutonomyLevel.Restricted

// Test: Resolve_SubagentDefinition_ReturnsDirectLevel
//   Arrange: SubagentDefinition with AutonomyLevel = Autonomous
//   Act: resolver.Resolve(definition)
//   Assert: Returns AutonomyLevel.Autonomous

// Test: Resolve_UnknownType_ReturnsFallbackFromConfig
//   Arrange: ISubagentProfileRegistry throws or returns null for unknown type
//            Config DefaultAutonomyLevel = "Supervised"
//   Act: resolver.Resolve(someUnregisteredType)
//   Assert: Returns AutonomyLevel.Supervised
```

### `ThreePhasePermissionResolverAutonomyTests` (Permission Integration)

**File:** `src/Content/Tests/Infrastructure.AI.Tests/Permissions/ThreePhasePermissionResolverAutonomyTests.cs`

These tests verify the tier rules work correctly when fed through the actual `ThreePhasePermissionResolver`. They follow the pattern in the existing `ThreePhasePermissionResolverTests.cs` (mock provider, real `GlobPatternMatcher`, real resolver).

```csharp
// Test: RestrictedAgent_NoOverrides_GetsAskForAnyTool
//   Arrange: AutonomyTierRuleProvider produces Ask("*") at Priority 0
//            Feed those rules into ThreePhasePermissionResolver via mock provider
//   Act: ResolvePermissionAsync("agent-1", "any_tool")
//   Assert: Decision.Behavior == Ask

// Test: RestrictedAgent_WithAllowOverride_StillGetsAskDueToPhaseOrdering
//   Arrange: Rules = [Ask("*", Priority 0), Allow("query_kg", Priority 10)]
//   Act: ResolvePermissionAsync("agent-1", "query_kg")
//   Assert: Decision.Behavior == Ask  (Ask phase matches "*" before Allow phase runs)
//   Note: This test documents the known limitation

// Test: AutonomousAgent_GetsAllowForAnyTool
//   Arrange: Rules = [Allow("*", Priority 0)]
//   Act: ResolvePermissionAsync("agent-1", "file_system")
//   Assert: Decision.Behavior == Allow

// Test: AutonomousAgent_WithManifestDenyRule_GetsDeny
//   Arrange: Tier rules = [Allow("*", Priority 0)]
//            Manifest rules = [Deny("bash", Priority 1)]
//   Act: ResolvePermissionAsync("agent-1", "bash")
//   Assert: Decision.Behavior == Deny  (Deny phase runs before Allow phase)

// Test: SupervisedAgent_WithSessionAllowOverride_StillGetsAsk
//   Arrange: Tier rules = [Ask("*", Priority 0)]
//            Session rules = [Allow("file_system", Priority 5)]
//   Act: ResolvePermissionAsync("agent-1", "file_system")
//   Assert: Decision.Behavior == Ask  (Ask phase matches "*" before session Allow)
//   Note: Documents that session Allow cannot override tier Ask in current resolver
```

---

## Implementation Details

### File 1: `src/Content/Application/Application.Core/Permissions/AutonomyTierRuleProvider.cs`

**Layer justification:** This is an Application-layer service. It depends on `IAutonomyTierResolver` and `IPermissionRuleProvider` (Application interfaces), `ToolPermissionRule` and related types (Domain), and `IOptionsMonitor<AppConfig>` (Microsoft.Extensions). No Infrastructure dependencies.

**Class:** `AutonomyTierRuleProvider` (sealed)

**Implements:** `IPermissionRuleProvider`

**Constructor dependencies:**
- `IAutonomyTierResolver` -- resolves the agent's tier
- `IOptionsMonitor<AppConfig>` -- reads tier policy configuration

**`Source` property:** Returns `PermissionRuleSource.AutonomyTier`

**`GetRulesAsync` method:**

1. Map `agentId` (string) to an `AutonomyLevel`. The provider needs a strategy for this since `IAutonomyTierResolver` accepts `SubagentType`, not string. Two approaches:
   - Try to parse `agentId` as a `SubagentType` via `Enum.TryParse`. This works if agent IDs are set to the type name (e.g., `"Explore"`, `"Execute"`).
   - If parsing fails, fall back to the default level from `PermissionsConfig`. This is the safe default -- unknown agents get the conservative tier.
   
   Implementation: attempt `Enum.TryParse<SubagentType>(agentId, ignoreCase: true, out var type)`. If successful, call `_tierResolver.Resolve(type)`. Otherwise, parse `PermissionsConfig.DefaultAutonomyLevel` to `AutonomyLevel` (defaulting to `Supervised` if that also fails).

2. Look up the `AutonomyTierPolicyConfig` from `AppConfig.AI.Permissions.TierPolicies` using the tier's name as key (e.g., `"Restricted"`, `"Supervised"`, `"Autonomous"`). Section 08 adds `TierPolicies` (a `Dictionary<string, AutonomyTierPolicyConfig>`) and `DefaultAutonomyLevel` (string) to `PermissionsConfig`.

3. If no policy config found for the tier, fall back: use `PermissionsConfig.DefaultBehavior` (existing field, defaults to `"Ask"`).

4. Parse the policy's `DefaultBehavior` string to `PermissionBehaviorType`. Generate the global baseline rule:
   ```
   new ToolPermissionRule("*", null, parsedBehavior, PermissionRuleSource.AutonomyTier, Priority: 0)
   ```

5. For each entry in `ToolOverrides` (if the policy has any), generate a tool-specific rule:
   ```
   new ToolPermissionRule(toolName, null, parsedOverrideBehavior, PermissionRuleSource.AutonomyTier, Priority: 10)
   ```

6. Return the list. The method is synchronous in practice (all data in memory) but returns `Task<IReadOnlyList<ToolPermissionRule>>` to satisfy the interface.

**XML documentation:** Include a `<remarks>` block that documents the known limitation with tool overrides and 3-phase resolution phase ordering. Explain that overrides at Priority 10 serve as audit metadata and will become enforceable when the resolver gains specificity-based cross-phase precedence.

### File 2: `src/Content/Infrastructure/Infrastructure.AI/Governance/DefaultAutonomyTierResolver.cs`

**Layer justification:** This is an Infrastructure-layer implementation. While the logic is simple (read from registry + config), it implements an Application interface and depends on `ISubagentProfileRegistry` (which may be backed by config or external sources in the future).

**Class:** `DefaultAutonomyTierResolver` (sealed)

**Implements:** `IAutonomyTierResolver`

**Constructor dependencies:**
- `ISubagentProfileRegistry` -- looks up subagent profiles by type
- `IOptionsMonitor<AppConfig>` -- reads `PermissionsConfig.DefaultAutonomyLevel` for fallback

**`Resolve(SubagentType agentType)` method:**
1. Call `_registry.GetProfile(agentType)` to get the `SubagentDefinition`
2. Read `definition.AutonomyLevel` (the new property from Section 01)
3. If the registry throws (unknown type) or the definition has no explicit level set, fall back to parsing `PermissionsConfig.DefaultAutonomyLevel` as `AutonomyLevel`
4. Default fallback if all else fails: `AutonomyLevel.Supervised`

**`Resolve(SubagentDefinition definition)` method:**
1. Directly return `definition.AutonomyLevel`
2. If the property is not set (default value -- depends on how Section 01 defines the default), fall back to config

Both overloads are synchronous (no I/O involved).

---

## DI Registration (Handled in Section 08)

For reference, these registrations are needed (Section 08 will implement them):

```csharp
// In Infrastructure.AI/DependencyInjection.cs:
services.AddSingleton<IAutonomyTierResolver, DefaultAutonomyTierResolver>();

// In Application.Core/DependencyInjection.cs:
services.AddSingleton<IPermissionRuleProvider, AutonomyTierRuleProvider>();
```

The `ThreePhasePermissionResolver` already aggregates all `IPermissionRuleProvider` instances via `IEnumerable<IPermissionRuleProvider>`. Adding a second provider alongside `ConfigBasedRuleProvider` requires no resolver changes.

---

## Configuration Types (Handled in Section 08)

The `AutonomyTierRuleProvider` reads from config types that Section 08 creates:

**`AutonomyTierPolicyConfig`** (new POCO in `Domain.Common/Config/AI/Permissions/`):
- `DefaultBehavior` -> string (e.g., `"Ask"`, `"Allow"`)
- `ToolOverrides` -> `Dictionary<string, string>?` (tool name -> behavior string)

**`PermissionsConfig` additions** (existing class, Section 08 adds):
- `DefaultAutonomyLevel` -> string (default: `"Supervised"`)
- `TierPolicies` -> `Dictionary<string, AutonomyTierPolicyConfig>`

Until Section 08 is implemented, the provider should handle null/missing config gracefully by falling back to safe defaults (Ask behavior, no overrides).

---

## Edge Cases and Error Handling

1. **Unknown agent ID:** Cannot be parsed to `SubagentType`. Fall back to config's `DefaultAutonomyLevel`. If that's also missing/invalid, use `AutonomyLevel.Supervised` (safe default).

2. **Missing tier policy in config:** No `TierPolicies` dictionary, or no entry for the resolved tier name. Fall back to `PermissionsConfig.DefaultBehavior` (existing field, defaults to `"Ask"`).

3. **Invalid behavior string in config:** `DefaultBehavior` or a `ToolOverrides` value cannot be parsed to `PermissionBehaviorType`. Log a warning and skip that rule (do not throw). For the global rule, fall back to Ask.

4. **Empty ToolOverrides:** Treat as no overrides. Generate only the global baseline rule.

5. **Registry throws for unknown SubagentType:** Catch the exception in `DefaultAutonomyTierResolver.Resolve(SubagentType)` and return the config fallback.

---

## File Summary

| File | Action | Purpose |
|------|--------|---------|
| `src/Content/Application/Application.Core/Permissions/AutonomyTierRuleProvider.cs` | Create | `IPermissionRuleProvider` implementation |
| `src/Content/Infrastructure/Infrastructure.AI/Governance/DefaultAutonomyTierResolver.cs` | Create | `IAutonomyTierResolver` implementation |
| `src/Content/Tests/Infrastructure.AI.Tests/Governance/AutonomyTierRuleProviderTests.cs` | Create | Tier rule provider tests |
| `src/Content/Tests/Infrastructure.AI.Tests/Governance/DefaultAutonomyTierResolverTests.cs` | Create | Tier resolver tests |
| `src/Content/Tests/Infrastructure.AI.Tests/Permissions/ThreePhasePermissionResolverAutonomyTests.cs` | Create | Permission integration tests |

## Verification Checklist

After implementing:
1. `dotnet build src/AgenticHarness.slnx` passes
2. All new tests pass: `dotnet test src/AgenticHarness.slnx --filter "AutonomyTierRuleProvider|DefaultAutonomyTierResolver|ThreePhasePermissionResolverAutonomy"`
3. Existing `ThreePhasePermissionResolverTests` still pass (no regression)
4. Existing `ConfigBasedRuleProviderTests` still pass
5. The `PermissionRuleSource.AutonomyTier` enum value compiles (depends on Section 01)
6. XML documentation is complete on all public types
