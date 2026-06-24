# Application.AI.Common.Tests

## What This Tests

Unit and integration tests for the **Application.AI.Common** project — the AI agent application layer containing MediatR pipeline behaviors, middleware, factories, services, telemetry, and domain interface contracts. Tests validate that agent execution contexts, tool permissions, content safety, prompt composition, context budgets, and observability behave correctly under both normal and error conditions.

## Test Organization

Files mirror the production project structure: `Behaviors/`, `MediatRBehaviors/`, `Middleware/`, `Services/`, `Factories/`, `Helpers/`, `OpenTelemetry/`, `Models/`, `Connectors/`, `Exceptions/`, `Extensions/`, `Config/`, `Interfaces/`, `MetaHarness/`, and `Integration/`. Each class targets a single production type. Naming convention: `MethodName_Scenario_ExpectedResult` for test methods.

## All Test Classes

| Test Class | What It Tests | Approx Test Count | Unit/Integration |
|------------|---------------|-------------------|------------------|
| `HookBehaviorTests` | Hook execution pipeline behavior | 5 | Unit |
| `HookBehaviorAgentTurnTests` | Hook behavior for agent turn commands | 4 | Unit |
| `MetaHarnessConfigTests` | MetaHarness configuration binding | 3 | Unit |
| `ConnectorOperationResultTests` | Connector result value object | 4 | Unit |
| `ConnectorToolAdapterTests` | Connector-to-AITool adapter bridge | 5 | Unit |
| `DependencyInjectionTests` | Service registration completeness | 3 | Unit |
| `AgentExecutionExceptionTests` | Exception construction/serialization | 3 | Unit |
| `AttackDetectionExceptionTests` | Attack detection exception | 3 | Unit |
| `ContentSafetyExceptionTests` | Content safety exception | 3 | Unit |
| `ContextBudgetExceededExceptionTests` | Budget exception messages | 3 | Unit |
| `HarnessProposalParsingExceptionTests` | Proposal parsing error | 3 | Unit |
| `McpConnectionExceptionTests` | MCP connection failure | 3 | Unit |
| `SkillNotFoundExceptionTests` | Skill lookup miss | 3 | Unit |
| `SkillParsingExceptionTests` | SKILL.md parsing error | 3 | Unit |
| `ToolExecutionExceptionTests` | Tool execution failure | 3 | Unit |
| `AgentContextExtensionsTests` | Extension methods on context | 4 | Unit |
| `ILoggerAgentExtensionsTests` | Agent-specific logging extensions | 3 | Unit |
| `AgentExecutionContextFactoryTests` | Factory creates valid contexts | 5 | Unit |
| `AgentFactoryTests` | Agent construction from skills | 6 | Unit |
| `PromptTemplateHelperTests` | Template placeholder substitution | 5 | Unit |
| `TokenEstimationHelperTests` | Token count estimation accuracy | 4 | Unit |
| `InterfaceRecordTests` | Interface record contract shapes | 3 | Unit |
| `AgentContextPropagationBehaviorTests` | Context flowing through pipeline | 4 | Unit |
| `AuditTrailBehaviorTests` | Audit log recording | 4 | Unit |
| `ContentSafetyBehaviorTests` | Content blocking in pipeline | 5 | Unit |
| `PromptInjectionBehaviorTests` | Injection detection blocking | 4 | Unit |
| `UnhandledExceptionBehaviorTests` | Global exception wrapping | 3 | Unit |
| `HarnessCandidateTests` | MetaHarness candidate model | 3 | Unit |
| `ObservabilityMiddlewareTests` | OTel span enrichment | 4 | Unit |
| `ToolDiagnosticsMiddlewareTests` | Tool call diagnostics | 4 | Unit |
| `ContextModelsTests` | Context model value objects | 4 | Unit |
| `ToolExecutionModelsTests` | Tool execution DTOs | 4 | Unit |
| `ToolExecutionRequestTests` | Request record validation | 3 | Unit |
| `AiSourceNamesTests` | Telemetry source name constants | 2 | Unit |
| `AiTelemetryConfiguratorTests` | Telemetry pipeline wiring | 3 | Unit |
| `MetricsInstrumentTests` | Custom metrics instruments | 6 | Unit |
| `AgentFrameworkSpanProcessorTests` | Span processor enrichment | 4 | Unit |
| `ConversationSpanProcessorTests` | Conversation-level spans | 4 | Unit |
| `AgentExecutionContextTests` | Context property access | 5 | Unit |
| `ToolPermissionFilterTests` | Tool filtering by permissions | 4 | Unit |
| `ContextBudgetTrackerTests` | Budget allocation and tracking | 6 | Unit |
| `TieredContextAssemblerTests` | 3-tier skill context loading | 5 | Unit |
| `AIToolConverterTests` | Keyed DI tool to AITool conversion | 4 | Unit |
| `ToolDescriptionBuilderTests` | Schema-based tool descriptions | 3 | Unit |
| `ContextBudgetTrackerIntegrationTests` | Budget across multiple operations | 4 | Integration |
| `MiddlewarePipelineIntegrationTests` | Full middleware chain end-to-end | 5 | Integration |
| `PromptTemplateHelperIntegrationTests` | Template with real skill data | 3 | Integration |
| `TokenEstimationHelperIntegrationTests` | Estimation against real prompts | 3 | Integration |
| `_SmokeTests` | Basic assembly load and DI wiring | 2 | Unit |

## Testing Patterns and Example

Tests follow Arrange-Act-Assert with Moq for dependency isolation and FluentAssertions for readable assertions.

```csharp
[Fact]
public async Task DenyDecision_ReturnsForbidden()
{
    // Arrange — configure mocks to simulate a denied permission
    _executionContext.Setup(c => c.AgentId).Returns("agent-1");
    _permissionService
        .Setup(s => s.ResolvePermissionAsync("agent-1", "test_tool", null, null, It.IsAny<CancellationToken>()))
        .ReturnsAsync(PermissionDecision.Deny("Tool is denied."));

    // Act — invoke the pipeline behavior
    var behavior = CreateBehavior();
    var result = await behavior.Handle(
        new TestToolRequest("test_tool"),
        CreateNext(),
        CancellationToken.None);

    // Assert — verify the result maps correctly
    result.IsSuccess.Should().BeFalse();
    result.FailureType.Should().Be(ResultFailureType.Forbidden);
    result.Errors.Should().Contain("Tool is denied.");
}
```

**Mocking pattern**: Class-level `Mock<T>` fields for shared dependencies, `CreateBehavior()` factory method for SUT construction, `CreateNext()` for MediatR delegate simulation. `Moq.Verify()` used to assert side effects like denial recording.

## How to Run

```bash
# All tests in this project
dotnet test src/Content/Tests/Application.AI.Common.Tests/Application.AI.Common.Tests.csproj

# With coverage
dotnet test src/Content/Tests/Application.AI.Common.Tests/Application.AI.Common.Tests.csproj --collect:"XPlat Code Coverage"

# Single class
dotnet test --filter "FullyQualifiedName~HookBehaviorTests"

# Single test
dotnet test --filter "FullyQualifiedName~HookBehaviorTests.PreToolUseHook_BlocksExecution"
```

## How to Add a New Test

1. Identify the production class under `Application.AI.Common` to test.
2. Create a file in the matching subfolder (e.g., `MediatRBehaviors/NewBehaviorTests.cs`).
3. Name: `{ProductionClassName}Tests.cs`. Class: `public sealed class {Name}Tests`.
4. Follow this skeleton:

```csharp
using FluentAssertions;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.MediatRBehaviors;

public sealed class NewBehaviorTests
{
    private readonly Mock<IDependency> _dep = new();

    [Fact]
    public async Task MethodName_Scenario_ExpectedResult()
    {
        // Arrange
        var sut = new NewBehavior(_dep.Object);

        // Act
        var result = await sut.Handle(...);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }
}
```

5. Run `dotnet test --filter "FullyQualifiedName~NewBehaviorTests"` to verify.

## Shared Helpers and Fixtures

| Helper | Location | Purpose |
|--------|----------|---------|
| `FakeChatClient` | `Fakes/FakeChatClient.cs` | In-memory IChatClient with canned responses and request history |
| `FakeChatClientFactory` | `Fakes/FakeChatClientFactory.cs` | IChatClientFactory returning FakeChatClient per deployment |

## Dependencies

| Package | Purpose |
|---------|---------|
| xunit | Test framework |
| Moq | Mocking |
| FluentAssertions | Fluent assertion syntax |
| coverlet.collector | Code coverage collection |
| Microsoft.NET.Test.Sdk | VS Test platform integration |
| xunit.runner.visualstudio | IDE test discovery |
