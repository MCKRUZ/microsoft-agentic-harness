# Infrastructure.AI.Governance.Tests

## What This Tests

Unit tests for the **Infrastructure.AI.Governance** project — adapter implementations that bridge the external AgentGovernance (AGT) library into the harness domain model. Tests validate policy engine evaluation, audit chain integrity, prompt injection detection accuracy, MCP tool security scanning (tool poisoning, hidden instructions, description injection), and API surface discovery for the AGT library.

## Test Organization

Files are organized into `Adapters/` (one test class per adapter) and root-level discovery tests. Each adapter test class verifies the thin adapter layer between the AGT library types and the harness `Domain.AI.Governance` contracts. Naming convention: `MethodName_Scenario_ExpectedResult`.

## All Test Classes

| Test Class | What It Tests | Approx Test Count | Unit/Integration |
|------------|---------------|-------------------|------------------|
| `AgtPolicyEngineAdapterTests` | Policy engine: load YAML, evaluate tool calls, allow/deny | 6 | Unit |
| `AgtAuditAdapterTests` | Audit logging: entry counting, chain integrity verification | 5 | Unit |
| `AgtPromptInjectionAdapterTests` | Injection detection: benign pass, override detect, confidence | 3 | Unit |
| `McpSecurityScannerAdapterTests` | MCP security: tool poisoning, hidden chars, description injection, base64, batch | 8 | Unit |
| `ApiDiscoveryTests` | Reflection-based AGT public API surface dump (diagnostic) | 5 | Unit |

## Testing Patterns and Example

Tests use real AGT library instances (not mocked) since the adapters are thin wrappers. The underlying `PolicyEngine`, `AuditLogger`, `PromptInjectionDetector`, and `McpSecurityScanner` are instantiated directly.

```csharp
[Fact]
public void EvaluateToolCall_DenyPolicy_ReturnsDenied()
{
    // Arrange — load a YAML policy that blocks execute_command
    var yaml = """
        name: block-dangerous
        rules:
          - name: block-exec
            condition: "tool == 'execute_command'"
            action: deny
            description: Execution tools are blocked
        """;
    _engine.LoadYaml(yaml);

    // Act — evaluate a tool call against the policy
    var decision = _adapter.EvaluateToolCall("agent-1", "execute_command");

    // Assert — verify denial with correct action
    Assert.False(decision.IsAllowed);
    Assert.Equal(GovernancePolicyAction.Deny, decision.Action);
}
```

**Mocking pattern**: No mocking needed. The AGT library is lightweight and deterministic enough for direct instantiation. Tests validate the adapter translation from AGT types (e.g., `PolicyDecision`) to domain types (e.g., `GovernanceDecision`). `ApiDiscoveryTests` uses reflection to dump the AGT public API for documentation and change detection.

## How to Run

```bash
# All tests in this project
dotnet test src/Content/Tests/Infrastructure.AI.Governance.Tests/Infrastructure.AI.Governance.Tests.csproj

# With coverage
dotnet test src/Content/Tests/Infrastructure.AI.Governance.Tests/Infrastructure.AI.Governance.Tests.csproj --collect:"XPlat Code Coverage"

# Single class
dotnet test --filter "FullyQualifiedName~AgtPolicyEngineAdapterTests"

# Single test
dotnet test --filter "FullyQualifiedName~AgtPolicyEngineAdapterTests.EvaluateToolCall_DenyPolicy_ReturnsDenied"
```

## How to Add a New Test

1. Identify the adapter under `Infrastructure.AI.Governance/Adapters/`.
2. Create or extend a file in `Adapters/` (e.g., `Adapters/NewAdapterTests.cs`).
3. Instantiate real AGT types directly — no mocking required.
4. Skeleton:

```csharp
using Infrastructure.AI.Governance.Adapters;
using Xunit;

namespace Infrastructure.AI.Governance.Tests.Adapters;

public sealed class NewAdapterTests
{
    private readonly NewAdapter _adapter;

    public NewAdapterTests()
    {
        _adapter = new NewAdapter(new UnderlyingAgtType());
    }

    [Fact]
    public void Method_BenignInput_ReturnsExpected()
    {
        var result = _adapter.Method("safe input");

        Assert.True(result.IsSafe);
    }

    [Fact]
    public void Method_MaliciousInput_DetectsThreat()
    {
        var result = _adapter.Method("malicious payload");

        Assert.False(result.IsSafe);
    }
}
```

5. Run: `dotnet test --filter "FullyQualifiedName~NewAdapterTests"`

## Shared Helpers and Fixtures

None — each test class creates its own AGT library instances in the constructor.

## Dependencies

| Package | Purpose |
|---------|---------|
| xunit | Test framework |
| Moq | Mocking (available but unused — real AGT instances used) |
| coverlet.collector | Code coverage |
| Microsoft.NET.Test.Sdk | VS Test platform |
| xunit.runner.visualstudio | IDE test discovery |
