# Section 20: Appsettings Configuration

## Overview

This section adds the `Escalation` and `Resilience` JSON config blocks to both `appsettings.json` files (`Presentation.AgentHub` and `Presentation.ConsoleUI`). These blocks provide concrete values for the config POCOs defined in section-04 (`EscalationConfig`, `ResilienceConfig`, and their child types) and wired via the Options pattern in section-19.

No code changes are needed beyond the JSON edits. The .NET Options pattern automatically binds nested properties from `AppConfig:AI:Governance:Escalation` and `AppConfig:AI:Resilience` into the strongly-typed config classes because `AIConfig` is already registered via `services.Configure<AIConfig>(configuration.GetSection("AppConfig:AI"))` in `Presentation.Common/Extensions/IServiceCollectionExtensions.cs`.

## Dependencies

- **Section 04 (config-and-validation):** Defines `EscalationConfig`, `EscalationPriorityConfig`, `ResilienceConfig`, `FallbackProviderConfig`, `ProviderCapabilitiesConfig`, `CircuitBreakerConfig`, `RetryConfig`, `TimeoutConfig`, `DegradedModeConfig`. Also modifies `GovernanceConfig` to add an `Escalation` property and `AIConfig` to add a `Resilience` property.
- **Section 01 (domain-escalation):** Defines the enums referenced by config values: `EscalationTimeoutAction`, `ApprovalStrategyType`, `EscalationPriority`, `EscalationWaitBehavior`.
- **Section 19 (di-registration):** Wires Options pattern binding and DI registration for all services.

## Files to Modify

| File | Change |
|------|--------|
| `src/Content/Presentation/Presentation.AgentHub/appsettings.json` | Add `Escalation` block under `AppConfig.AI.Governance`, add `Resilience` block under `AppConfig.AI` |
| `src/Content/Presentation/Presentation.ConsoleUI/appsettings.json` | Add `Escalation` block under `AppConfig.AI.Governance`, add `Resilience` block under `AppConfig.AI` |

---

## Tests First

The TDD plan specifies three config-binding tests to verify that the JSON values round-trip correctly through the Options pattern.

```csharp
// Test: EscalationConfig_BindsFromAppsettings
//   Arrange: Build IConfiguration from in-memory JSON with the Escalation block structure.
//            Bind to EscalationConfig via configuration.GetSection().Get<T>().
//   Assert: Enabled == true, DefaultTimeoutSeconds == 300,
//           DefaultTimeoutAction == "DenyAndEscalate", DefaultApprovalStrategy == "AnyOf",
//           PriorityLevels has entries for "Informational", "Blocking", "Critical".

// Test: ResilienceConfig_BindsFromAppsettings
//   Arrange: Build IConfiguration from in-memory JSON with the Resilience block structure.
//            Bind to ResilienceConfig via configuration.GetSection().Get<T>().
//   Assert: Enabled == false (default for template),
//           FallbackChain has 2 entries, first is AzureOpenAI/gpt-4o, second is AzureAIInference/claude-sonnet,
//           CircuitBreaker.FailureRatio == 0.5, Retry.MaxAttempts == 2, Timeout.PerAttemptSeconds == 30.

// Test: FallbackProviderConfig_BindsClientTypeAndDeploymentId
//   Arrange: Build IConfiguration with a single FallbackChain entry.
//   Assert: entry.ClientType == AIAgentFrameworkClientType.AzureOpenAI,
//           entry.DeploymentId == "gpt-4o",
//           entry.Capabilities.SupportsToolCalling == true.
```

---

## Implementation Details

### Config Binding Architecture

The existing config binding pipeline works as follows:

1. `Program.cs` calls `builder.Services.GetServices()` (defined in `Presentation.Common/Extensions/IServiceCollectionExtensions.cs`)
2. `GetServices()` calls `RegisterConfigSections(config)` which includes: `services.Configure<AIConfig>(configuration.GetSection("AppConfig:AI"))`
3. .NET Options pattern recursively binds all nested properties from the JSON section into the `AIConfig` class
4. `AIConfig.Governance` (type `GovernanceConfig`) picks up `AppConfig:AI:Governance:*`
5. `GovernanceConfig.Escalation` (type `EscalationConfig`, added in section-04) picks up `AppConfig:AI:Governance:Escalation:*`
6. `AIConfig.Resilience` (type `ResilienceConfig`, added in section-04) picks up `AppConfig:AI:Resilience:*`

### Escalation Block

Add inside `AppConfig.AI.Governance`:

```json
"Escalation": {
  "Enabled": true,
  "DefaultTimeoutSeconds": 300,
  "DefaultTimeoutAction": "DenyAndEscalate",
  "DefaultApprovalStrategy": "AnyOf",
  "AuditStoragePath": ".agent-sessions/escalations",
  "PriorityLevels": {
    "Informational": {
      "TimeoutSeconds": 0,
      "Async": true,
      "EscalateToAll": false
    },
    "Blocking": {
      "TimeoutSeconds": 300,
      "Async": false,
      "EscalateToAll": false
    },
    "Critical": {
      "TimeoutSeconds": 600,
      "Async": false,
      "EscalateToAll": true
    }
  }
}
```

Design notes:
- `Enabled: true` -- escalation is on by default. No external dependencies needed.
- `Informational` priority has `TimeoutSeconds: 0` and `Async: true` -- fire-and-forget notifications.
- `Blocking` priority uses 300s (5 min) timeout -- agent blocks while awaiting human response.
- `Critical` priority gets 600s (10 min) and `EscalateToAll: true` -- all approvers notified simultaneously.
- `DefaultTimeoutAction: "DenyAndEscalate"` -- safest default when timeout expires.
- `DefaultApprovalStrategy: "AnyOf"` -- single approver response resolves the escalation.

### Resilience Block

Add as sibling of `Governance` under `AppConfig.AI`:

```json
"Resilience": {
  "Enabled": false,
  "FallbackChain": [
    {
      "ClientType": "AzureOpenAI",
      "DeploymentId": "gpt-4o",
      "Capabilities": {
        "SupportsToolCalling": true,
        "SupportsStreaming": true,
        "SupportsVision": true,
        "MaxTokens": 128000
      }
    },
    {
      "ClientType": "AzureAIInference",
      "DeploymentId": "claude-sonnet",
      "Capabilities": {
        "SupportsToolCalling": true,
        "SupportsStreaming": true,
        "SupportsVision": false,
        "MaxTokens": 200000
      }
    }
  ],
  "CircuitBreaker": {
    "FailureRatio": 0.5,
    "SamplingDurationSeconds": 30,
    "MinimumThroughput": 5,
    "BreakDurationSeconds": 60
  },
  "Retry": {
    "MaxAttempts": 2,
    "BaseDelaySeconds": 1.0,
    "BackoffType": "Exponential"
  },
  "Timeout": {
    "PerAttemptSeconds": 30
  },
  "DegradedMode": {
    "RetryQueueTtlSeconds": 300,
    "MaxQueueSize": 100
  }
}
```

Design notes:
- `Enabled: false` -- resilience is OFF by default. Template consumers must opt-in and configure provider endpoints/keys.
- `FallbackChain` has two entries showing the pattern: primary Azure OpenAI, fallback Azure AI Inference (for Claude).
- The second provider declares `SupportsVision: false` to demonstrate capability diffing.
- `CircuitBreaker` uses Polly v8 standard defaults: 50% failure ratio, 30s sampling, 5 minimum throughput, 60s break.
- `Retry` uses 2 max attempts with exponential backoff starting at 1s. Jitter added at runtime.
- `Timeout` at 30s per attempt is generous for LLM API calls.
- `DegradedMode` allows 100 queued requests with 5-minute TTL.

### Placement in JSON

**AgentHub** (`src/Content/Presentation/Presentation.AgentHub/appsettings.json`):

```json
"AI": {
  "AgentFramework": { ... },
  "Governance": {
    ... existing properties ...,
    "Escalation": { ... }
  },
  "Resilience": { ... },
  "Permissions": { ... },
  ...
}
```

**ConsoleUI** (`src/Content/Presentation/Presentation.ConsoleUI/appsettings.json`):

Same placement. Both hosts get identical config since they share the same config class hierarchy.

---

## Key Design Decisions

1. **Escalation enabled, Resilience disabled by default.** Escalation works with in-memory state and no external dependencies -- safe to enable. Resilience requires actual provider endpoints and API keys -- must be opt-in.

2. **Both hosts get identical config.** Template consumers can diverge per-host, but the template ships with identical defaults.

3. **No new `services.Configure<>()` calls needed for nested binding.** The existing `services.Configure<AIConfig>(...)` handles the full tree. Section-19 adds explicit `Configure<EscalationConfig>` and `Configure<ResilienceConfig>` for direct injection via `IOptionsMonitor<T>`.

4. **`ClientType` uses enum name strings.** `"AzureOpenAI"` and `"AzureAIInference"` are string representations of `AIAgentFrameworkClientType` enum members. .NET config binding handles enum-from-string conversion automatically.

5. **Capability declarations are optional.** Per section-14, if capabilities are not declared, the registry assumes full capability. The template includes them to demonstrate the pattern.

6. **PriorityLevels uses string keys.** `Dictionary<string, EscalationPriorityConfig>` is keyed by `EscalationPriority` enum name strings.

---

## Verification

```
dotnet build src/AgenticHarness.slnx
dotnet test src/Content/Tests/Presentation.Common.Tests/Presentation.Common.Tests.csproj --filter "FullyQualifiedName~IServiceCollectionExtensionsTests"
```

---

## Implementation Notes (Post-Implementation)

### Deviations from Plan

1. **Tests placed in IServiceCollectionExtensionsTests** — Plan suggested a "ConfigBinding" filter. Tests were added to the existing `IServiceCollectionExtensionsTests` class alongside the section-19 Options binding tests, which is the natural home for config binding verification.

2. **Code review fixes** — Added missing `Blocking` priority entries to the `EscalationConfig_BindsFullStructure_FromAppsettings` test (only Informational and Critical were originally tested). Added `DegradedMode` assertions (`RetryQueueTtlSeconds`, `MaxQueueSize`) to `ResilienceConfig_BindsFullStructure_FromAppsettings`.

### Test Counts
- 3 new tests: `EscalationConfig_BindsFullStructure_FromAppsettings`, `ResilienceConfig_BindsFullStructure_FromAppsettings`, `FallbackProviderConfig_BindsCapabilities`
- All 14 IServiceCollectionExtensionsTests passing (includes 11 pre-existing + section-19 tests)
