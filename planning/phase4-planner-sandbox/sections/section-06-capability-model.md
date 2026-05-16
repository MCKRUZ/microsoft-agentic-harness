# Section 06: Capability Model

## Overview

This section extends the existing `ToolPermissionBehavior` MediatR pipeline behavior with capability-based security checks. It adds a new enforcement layer that validates tools against declared capabilities (file I/O, network, subprocess, etc.) before execution. This is distinct from the existing permission system (which answers "is this agent allowed to use this tool?") -- capability enforcement answers "does this tool's requested actions fall within the granted capability set?"

The core pattern: tools declare capabilities via `[ToolCapabilityAttribute]` at compile time, operators can restrict those further via `appsettings.json` overrides, and the `ToolPermissionBehavior` validates the session's granted capabilities against the tool's requirements using deny-overrides-allow (Deno-style) semantics.

### Dependencies

- **Section 01 (Domain Models)**: `ToolCapability` flags enum, `ToolPermissionProfile` record, `SandboxIsolationLevel` enum, `ToolCapabilityAttribute`
- **Section 02 (Application Interfaces)**: `ICapabilityEnforcer` interface

This section blocks sections 07, 08, and 10.

---

## Implementation (Actual)

### Files Created

| File | Layer | Purpose |
|------|-------|---------|
| `Domain.Common/Config/AI/Sandbox/SandboxConfig.cs` | Domain | Strongly-typed config POCO for sandbox enforcement (`DefaultGrantedCapabilities`, `ToolOverrides`) |
| `Domain.Common/Config/AI/Sandbox/ToolOverrideConfig.cs` | Domain | Per-tool override from appsettings (denied capabilities, paths, hosts, isolation) |
| `Application.AI.Common/Interfaces/MediatR/IResourceScopedToolRequest.cs` | Application | Extended tool request with `RequestedPaths` and `RequestedHosts` for path/host validation |
| `Application.AI.Common/Services/Sandbox/ToolPermissionProfileResolver.cs` | Application | Singleton; merges `[ToolCapabilityAttribute]` + `ToolOverrideConfig` into effective `ToolPermissionProfile` |
| `Application.AI.Common/Services/Sandbox/CapabilityEnforcer.cs` | Application | Scoped; implements `ICapabilityEnforcer` with deny-overrides-allow enforcement |
| `Application.AI.Common.Tests/Services/Sandbox/ToolPermissionProfileResolverTests.cs` | Tests | 9 tests for profile resolution merging |
| `Application.AI.Common.Tests/Behaviors/CapabilityEnforcementTests.cs` | Tests | 13 tests including 5 adversarial security tests |

### Files Modified

| File | Change |
|------|--------|
| `Application.AI.Common/MediatRBehaviors/ToolPermissionBehavior.cs` | Added `ICapabilityEnforcer` + `IOptionsMonitor<SandboxConfig>` injection; capability check after Allow decision using inlined `Enum.TryParse` loop |
| `Application.AI.Common/DependencyInjection.cs` | Added `AddOptions<SandboxConfig>()`, `AddSingleton<ToolPermissionProfileResolver>()`, `AddScoped<ICapabilityEnforcer, CapabilityEnforcer>()` |
| `Application.AI.Common.Tests/Behaviors/ToolPermissionBehaviorTests.cs` | Added mock fields for `ICapabilityEnforcer` and `IOptionsMonitor<SandboxConfig>` to match updated constructor |

### Key Design Decisions

1. **String-based capability names in SandboxConfig**: `DefaultGrantedCapabilities` is `List<string>` (not `ToolCapability` flags) to avoid Domain.Common → Domain.AI dependency direction violation. Parsed to enum at runtime in the behavior.

2. **Inlined Enum.TryParse in ToolPermissionBehavior**: The behavior parses capability strings itself rather than calling `ToolPermissionProfileResolver.ParseCapabilities()` to avoid a concrete `Application.AI.Common.Services.Sandbox` dependency in the MediatR behaviors layer.

3. **Config binding deferred to Section 16**: `AddOptions<SandboxConfig>()` registers with coded defaults (all 8 capabilities granted). Section 16 will add `.Bind(configuration.GetSection("Sandbox"))`.

4. **CapabilityEnforcer depends on concrete ToolPermissionProfileResolver**: Acceptable for a single-implementation internal service — no interface needed for the resolver.

5. **All 8 capabilities granted by default**: Permissive for development. Production restricts via appsettings overrides.

### Security Fixes (from Code Review)

1. **Path traversal bypass (HIGH)**: Original `NormalizePath` used simple `Replace('\\', '/')` which didn't resolve `..` segments. `./workspace/../../../etc/passwd` passed prefix check. Fixed with segment-collapsing normalization that drops `..` above root.

2. **Host port stripping (MEDIUM)**: Added `StripPort()` helper so `admin.example.com:8080` correctly matches denial of `admin.example.com`.

3. **Concrete dependency removal (HIGH)**: Removed `using Application.AI.Common.Services.Sandbox` from `ToolPermissionBehavior` — inlined `Enum.TryParse` loop instead of calling resolver static method.

---

## Tests (Actual)

### ToolPermissionProfileResolverTests.cs — 9 tests

- `Resolve_NoAttribute_NoOverride_ReturnsDefaultProfile`
- `Resolve_AttributeOnly_ReturnsAttributeValues`
- `Resolve_OverrideOnly_AppliesOverrideValues`
- `Resolve_DeniedCapabilities_RemovedFromAttribute`
- `Resolve_MinimumIsolation_ElevatesButNeverDowngrades`
- `Resolve_OverridePaths_MergedIntoProfile`
- `ParseCapabilities_ValidNames_ReturnsCombinedFlags`
- `ParseCapabilities_InvalidNames_Ignored`
- `ParseCapabilities_Empty_ReturnsNone`

### CapabilityEnforcementTests.cs — 13 tests

**Core enforcement:**
- `AllCapabilitiesGranted_PassesThrough`
- `MissingCapability_ReturnsFail`
- `DeniedPath_ReturnsFail`
- `DeniedHost_ReturnsFail`

**Override behavior:**
- `AppsettingsOverride_RestrictsAttributeDefaults`
- `AppsettingsOverride_CannotExpandBeyondAttribute`

**Profile resolution:**
- `Resolution_AttributeFallbackWhenNoOverride`
- `Resolution_OverrideTakesPrecedence`

**Adversarial / edge cases:**
- `PathTraversal_DeniedEvenWhenPrefixMatches` — `./workspace/../../../etc/passwd` blocked
- `MixedSeparators_NormalizedCorrectly` — backslashes normalized to forward slashes
- `HostWithPort_MatchesDeniedHost` — port stripped before matching
- `EmptyRequestedPaths_PassesThrough` — empty list doesn't trigger path validation
- `UnregisteredTool_NoCapabilitiesRequired_PassesThrough` — unknown tools pass (no attribute = no requirements)

### ToolPermissionBehaviorTests.cs — 8 existing tests updated

All 8 existing tests updated with new constructor parameters (mock `ICapabilityEnforcer` returns `Result.Success()` by default).

**Total: 30 tests, 0 failures.**

---

## Configuration Shape (from Section 16)

```json
{
  "Sandbox": {
    "DefaultGrantedCapabilities": ["FileRead", "FileWrite", "NetworkAccess", "Subprocess", "EnvRead", "DatabaseRead", "DatabaseWrite", "LlmInvocation"],
    "ToolOverrides": {
      "file_system": {
        "DeniedCapabilities": ["NetworkAccess", "Subprocess"],
        "AllowedPaths": ["./workspace"],
        "DeniedPaths": ["./workspace/.secrets"],
        "MinimumIsolation": "Process"
      }
    }
  }
}
```
