# Section 14: Provider Capability Registry

## Overview

The `ProviderCapabilityRegistry` is a config-driven component in `Infrastructure.AI/Resilience/` that maps LLM providers to their declared capabilities and computes capability diffs when the `ResilientChatClient` falls back from one provider to another. The diff populates `FallbackMetadata.DisabledCapabilities`, letting agents adapt behavior (e.g., skip vision-dependent tool calls when the active provider lacks vision support).

## Dependencies

- **Section 04 (Config and Validation):** `ResilienceConfig`, `FallbackProviderConfig`, `ProviderCapabilitiesConfig` must exist in `Domain.Common/Config/AI/Resilience/`.
- **Section 07 (Resilience Interfaces):** No direct interface dependency -- the registry is a concrete class, not behind an interface (it is consumed only by `ResilientChatClient` and `ResilientChatClientProvider` in the same Infrastructure.AI project).
- **Section 02 (Domain Resilience):** `FallbackMetadata` record with its `DisabledCapabilities` property of type `IReadOnlySet<string>`.

## File Paths

| File | Action |
|------|--------|
| `src/Content/Infrastructure/Infrastructure.AI/Resilience/ProviderCapabilityRegistry.cs` | **Create** |
| `src/Content/Tests/Infrastructure.AI.Tests/Resilience/ProviderCapabilityRegistryTests.cs` | **Create** |

## Tests First

Create `src/Content/Tests/Infrastructure.AI.Tests/Resilience/ProviderCapabilityRegistryTests.cs`.

Four test cases cover the full contract:

```csharp
/// <summary>
/// Tests for <see cref="ProviderCapabilityRegistry"/>.
/// Uses IOptionsMonitor<ResilienceConfig> to supply FallbackChain with
/// ProviderCapabilitiesConfig per provider.
/// </summary>
public class ProviderCapabilityRegistryTests
{
    // Test: GetCapabilities_ConfiguredProvider_ReturnsFromConfig
    //
    // Arrange: ResilienceConfig with a FallbackChain entry for "azure-openai"
    //          that declares SupportsToolCalling=true, SupportsVision=true,
    //          SupportsStreaming=true, MaxTokens=128000.
    // Act:     Call GetCapabilities("azure-openai").
    // Assert:  Returned ProviderCapabilitiesConfig matches the configured values.

    // Test: GetCapabilities_UnconfiguredProvider_ReturnsFullCapabilities
    //
    // Arrange: ResilienceConfig with no entry for "unknown-provider".
    // Act:     Call GetCapabilities("unknown-provider").
    // Assert:  Returned ProviderCapabilitiesConfig has all boolean capabilities
    //          set to true and MaxTokens set to int.MaxValue (or a sensible default).
    //          This "assume full capability" behavior avoids false restriction.

    // Test: DiffCapabilities_PrimaryHasVision_FallbackDoesNot_ReportsDisabled
    //
    // Arrange: Primary provider config with SupportsVision=true.
    //          Fallback provider config with SupportsVision=false.
    // Act:     Call DiffCapabilities("primary", "fallback").
    // Assert:  Returned IReadOnlySet<string> contains "vision".
    //          Does NOT contain "tool_calling" or "streaming" if both providers
    //          support those.

    // Test: DiffCapabilities_IdenticalProviders_NothingDisabled
    //
    // Arrange: Two providers with identical capability configs.
    // Act:     Call DiffCapabilities("providerA", "providerB").
    // Assert:  Returned IReadOnlySet<string> is empty.
}
```

### Test Setup Pattern

Mock `IOptionsMonitor<ResilienceConfig>` to return a `ResilienceConfig` with a `FallbackChain` array containing `FallbackProviderConfig` entries. Each entry has a `DeploymentId` (used as the provider name key), a `ClientType`, and an optional `Capabilities` of type `ProviderCapabilitiesConfig`.

```csharp
// Example test arrangement (pseudocode for clarity):
var config = new ResilienceConfig
{
    FallbackChain = new[]
    {
        new FallbackProviderConfig
        {
            ClientType = AIAgentFrameworkClientType.AzureOpenAI,
            DeploymentId = "gpt-4o",
            Capabilities = new ProviderCapabilitiesConfig
            {
                SupportsToolCalling = true,
                SupportsStreaming = true,
                SupportsVision = true,
                MaxTokens = 128000
            }
        },
        new FallbackProviderConfig
        {
            ClientType = AIAgentFrameworkClientType.Anthropic,
            DeploymentId = "claude-sonnet",
            Capabilities = new ProviderCapabilitiesConfig
            {
                SupportsToolCalling = true,
                SupportsStreaming = true,
                SupportsVision = false,
                MaxTokens = 200000
            }
        }
    }
};

var monitor = Mock.Of<IOptionsMonitor<ResilienceConfig>>(m => m.CurrentValue == config);
var registry = new ProviderCapabilityRegistry(monitor);
```

## Implementation Details

### ProviderCapabilityRegistry

**File:** `src/Content/Infrastructure/Infrastructure.AI/Resilience/ProviderCapabilityRegistry.cs`

**Namespace:** `Infrastructure.AI.Resilience`

**Constructor dependencies:**
- `IOptionsMonitor<ResilienceConfig>` -- reads `FallbackChain` entries at resolution time (supports config reload).

**Public API:**

```csharp
/// <summary>
/// Config-driven registry mapping LLM provider deployment IDs to their declared
/// capabilities. Used by <see cref="ResilientChatClient"/> to compute
/// <see cref="FallbackMetadata.DisabledCapabilities"/> when falling back.
/// </summary>
public sealed class ProviderCapabilityRegistry
{
    /// <summary>
    /// Returns the declared capabilities for the given provider deployment ID.
    /// If the provider is not in the fallback chain config, returns a
    /// "full capability" default (all features enabled) to avoid false restrictions.
    /// </summary>
    public ProviderCapabilitiesConfig GetCapabilities(string deploymentId);

    /// <summary>
    /// Computes the set of capability names that are available on the
    /// <paramref name="primaryDeploymentId"/> but NOT available on the
    /// <paramref name="activeDeploymentId"/>. The returned strings use
    /// well-known keys: "tool_calling", "streaming", "vision".
    /// Returns an empty set if the active provider is equally or more capable.
    /// </summary>
    public IReadOnlySet<string> DiffCapabilities(
        string primaryDeploymentId,
        string activeDeploymentId);
}
```

### Capability Key Constants

Define well-known capability string constants as `internal static` fields on `ProviderCapabilityRegistry` (or a nested static class) to avoid magic strings:

- `"tool_calling"` -- maps to `ProviderCapabilitiesConfig.SupportsToolCalling`
- `"streaming"` -- maps to `ProviderCapabilitiesConfig.SupportsStreaming`
- `"vision"` -- maps to `ProviderCapabilitiesConfig.SupportsVision`

### GetCapabilities Logic

1. Read `ResilienceConfig.FallbackChain` from the options monitor.
2. Find the first `FallbackProviderConfig` where `DeploymentId` matches the input (case-insensitive ordinal).
3. If found and `Capabilities` is non-null, return it.
4. If not found or `Capabilities` is null, return a default `ProviderCapabilitiesConfig` with all booleans `true` and `MaxTokens = int.MaxValue`. This "assume full capability" design means unconfigured providers are never artificially restricted.

### DiffCapabilities Logic

1. Call `GetCapabilities` for both deployment IDs.
2. Build a `HashSet<string>`.
3. For each capability property: if the primary has it `true` and the active has it `false`, add the corresponding capability key string.
4. Return the set as `IReadOnlySet<string>`.

This set is what `ResilientChatClient` assigns to `FallbackMetadata.DisabledCapabilities` when it falls back from the primary to an alternate provider.

### ProviderCapabilitiesConfig Prerequisite

This config class is created in section-04. For reference, it lives at `src/Content/Domain/Domain.Common/Config/AI/Resilience/ProviderCapabilitiesConfig.cs` and has these properties:

- `SupportsToolCalling` (bool, default `true`)
- `SupportsStreaming` (bool, default `true`)
- `SupportsVision` (bool, default `true`)
- `MaxTokens` (int, default `int.MaxValue`)
- `SupportedMediaTypes` (IReadOnlyList<string>, default empty -- reserved for future use)

### FallbackMetadata.DisabledCapabilities Prerequisite

This property is defined in section-02 as part of the `FallbackMetadata` record at `src/Content/Domain/Domain.AI/Resilience/FallbackMetadata.cs`. The type is `IReadOnlySet<string>`.

## How Consumers Use the Registry

The `ResilientChatClient` (section-12) and `ResilientChatClientProvider` (section-16) use the registry as follows:

1. `ResilientChatClientProvider` constructs `ProviderCapabilityRegistry` (injected via DI or created inline from the same `IOptionsMonitor<ResilienceConfig>`).
2. It passes the registry to `ResilientChatClient` at construction time.
3. When `ResilientChatClient.GetResponseAsync` falls back from the primary provider to an alternate, it calls `registry.DiffCapabilities(primaryDeploymentId, activeDeploymentId)` and sets the result on `FallbackMetadata.DisabledCapabilities`.
4. The agent or orchestration layer inspects `FallbackMetadata.DisabledCapabilities` to decide whether to skip vision-dependent operations, disable streaming, etc.

## DI Registration

The registry is registered in section-19 within `Infrastructure.AI/DependencyInjection.cs` as a singleton:

```csharp
services.AddSingleton<ProviderCapabilityRegistry>();
```

No interface is needed -- it is consumed only within `Infrastructure.AI` by `ResilientChatClient` and `ResilientChatClientProvider`. If cross-layer consumption is needed later, extract an interface at that time (YAGNI).

## Design Decisions

1. **No interface extraction.** The registry is a pure config reader with no external dependencies beyond `IOptionsMonitor<ResilienceConfig>`. It has no side effects and is trivially testable as a concrete class. Adding an interface would be premature abstraction.

2. **Deployment ID as the key, not ClientType.** A single `ClientType` (e.g., `AzureOpenAI`) could have multiple deployments with different capabilities (GPT-4o with vision vs. GPT-3.5 without). Keying by `DeploymentId` gives the correct granularity.

3. **Assume full capability for unconfigured providers.** Returning a restrictive default for unknown providers would silently break features. Returning a permissive default means the worst case is attempting an unsupported operation and getting a runtime error with a clear cause, which is preferable to silent capability suppression.

4. **Config-driven, not code-driven.** Template consumers declare capabilities in `appsettings.json` under `FallbackProviderConfig.Capabilities`. No code changes needed to add a new provider to the chain with its capabilities.

---

## Verification

```
dotnet build src/Content/Infrastructure/Infrastructure.AI/Infrastructure.AI.csproj
dotnet test src/Content/Tests/Infrastructure.AI.Tests/Infrastructure.AI.Tests.csproj --filter "FullyQualifiedName~ProviderCapabilityRegistryTests"
```

---

## Implementation Notes

**Deviations from plan:**
- Capability constants are `public const` (not `internal static`) since tests and downstream consumers reference them.
- `FullCapabilities` default has `SupportsVision = true` and `MaxTokens = int.MaxValue` as specified, overriding the `ProviderCapabilitiesConfig` class defaults (`SupportsVision = false`, `MaxTokens = 4096`).
- `DiffCapabilities` returns `ImmutableHashSet<string>.Empty` when no diff exists (avoids allocating empty HashSet).

**Final test count:** 4 (all passing, ~124ms total duration)
