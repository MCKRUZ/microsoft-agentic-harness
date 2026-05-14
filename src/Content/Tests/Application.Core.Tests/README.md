# Application.Core.Tests

## What This Tests

Unit and integration tests for the **Application.Core** project — the CQRS command/query layer that orchestrates agent turns, multi-turn conversations, orchestrated tasks, and meta-harness optimization. Tests validate handler logic, FluentValidation validators, command record construction, agent registry integration, and the full MediatR pipeline including validation behaviors.

## Test Organization

Files are organized by domain area: `CQRS/` (handlers, validators, commands), `CQRS/MetaHarness/` (optimization handlers), `Agents/` (definitions), `Helpers/` (test doubles), and `Integration/` (cross-component verification). Naming convention: `MethodName_Scenario_ExpectedResult`.

## All Test Classes

| Test Class | What It Tests | Approx Test Count | Unit/Integration |
|------------|---------------|-------------------|------------------|
| `ExecuteAgentTurnCommandHandlerTests` | Single agent turn execution (success, failure, history) | 10 | Unit |
| `ExecuteAgentTurnCommandHandler_ExtractContentTests` | Content extraction from agent responses | 4 | Unit |
| `ExecuteAgentTurnCommandHandler_RegistryTests` | Handler interaction with agent registry | 4 | Unit |
| `ExecuteAgentTurnCommandTests` | Command record construction and properties | 3 | Unit |
| `ExecuteAgentTurnCommandValidatorTests` | FluentValidation rules for turn command | 5 | Unit |
| `ExecuteAgentTurnCommandValidatorTests_Extended` | Edge-case validator scenarios | 4 | Unit |
| `RunConversationCommandHandlerTests` | Multi-turn conversation orchestration | 6 | Unit |
| `RunConversationCommandTests` | Conversation command record shape | 3 | Unit |
| `RunConversationCommandValidatorTests` | Validator for conversation commands | 4 | Unit |
| `RunConversationCommandValidatorTests_Extended` | Edge-case conversation validation | 3 | Unit |
| `RunOrchestratedTaskCommandHandlerTests` | Task orchestration handler logic | 5 | Unit |
| `RunOrchestratedTaskCommandHandler_ExtractContentTests` | Content extraction from orchestrated tasks | 3 | Unit |
| `RunOrchestratedTaskCommandTests` | Orchestrated task command shape | 3 | Unit |
| `RunOrchestratedTaskCommandValidatorTests` | Validation rules for task commands | 4 | Unit |
| `RunOrchestratedTaskCommandValidatorTests_Extended` | Edge-case task validation | 3 | Unit |
| `RunHarnessOptimizationCommandHandlerTests` | Meta-harness optimization main loop | 6 | Unit |
| `RunHarnessOptimizationCommandHandler_ConstructorTests` | Handler construction validation | 3 | Unit |
| `RunHarnessOptimizationCommandHandler_EarlyStopTests` | Early termination conditions | 4 | Unit |
| `RunHarnessOptimizationCommandHandler_LearningsTests` | Learning extraction from iterations | 4 | Unit |
| `RunHarnessOptimizationCommandHandler_RegressionTests` | Regression detection in optimization | 4 | Unit |
| `RunHarnessOptimizationCommandHandler_SnapshotTests` | Configuration snapshot management | 3 | Unit |
| `RunHarnessOptimizationCommandTests` | Optimization command record | 3 | Unit |
| `RunHarnessOptimizationCommandValidatorTests` | Validator for optimization command | 3 | Unit |
| `AgentDefinitionsTests` | Agent definition loading and properties | 5 | Unit |
| `AgentDefinitionsTests_Extended` | Extended agent definition scenarios | 4 | Unit |
| `AgentPipelineIntegrationTests` | Full CQRS pipeline end-to-end | 4 | Integration |
| `CommandRecordIntegrationTests` | Command record serialization round-trip | 3 | Integration |
| `ValidatorIntegrationTests` | Validator discovery and execution | 3 | Integration |
| `DependencyInjectionTests` | Service registration completeness | 3 | Unit |
| `_SmokeTests` | Assembly load verification | 2 | Unit |

## Testing Patterns and Example

Tests use `TestableAIAgent` (a concrete `AIAgent` subclass with configurable responses) and Moq for factory/registry dependencies. Handlers are tested in isolation with mocked factories.

```csharp
[Fact]
public async Task Handle_ValidRequest_ReturnsSuccessResult()
{
    // Arrange — create agent that returns fixed text, wire factory
    var agent = new TestableAIAgent("Agent response text");
    _agentFactory
        .Setup(f => f.CreateAgentFromSkillAsync(
            "TestAgent",
            It.IsAny<SkillAgentOptions>(),
            It.IsAny<CancellationToken>()))
        .ReturnsAsync(agent);

    var command = CreateCommand();

    // Act — execute the handler
    var result = await _handler.Handle(command, CancellationToken.None);

    // Assert — verify success and response content
    result.Success.Should().BeTrue();
    result.Response.Should().Be("Agent response text");
    result.Error.Should().BeNull();
}
```

**Mocking pattern**: `Mock<IAgentFactory>` for agent construction, `Mock<IAgentMetadataRegistry>` for agent lookup, `TestableAIAgent` as the concrete double for `AIAgent`. `NullLogger<T>.Instance` for logging. Helper method `CreateCommand()` builds valid commands with configurable overrides.

## How to Run

```bash
# All tests in this project
dotnet test src/Content/Tests/Application.Core.Tests/Application.Core.Tests.csproj

# With coverage
dotnet test src/Content/Tests/Application.Core.Tests/Application.Core.Tests.csproj --collect:"XPlat Code Coverage"

# Single class
dotnet test --filter "FullyQualifiedName~ExecuteAgentTurnCommandHandlerTests"

# Single test
dotnet test --filter "FullyQualifiedName~ExecuteAgentTurnCommandHandlerTests.Handle_ValidRequest_ReturnsSuccessResult"
```

## How to Add a New Test

1. Identify the CQRS handler, validator, or command under `Application.Core`.
2. Create a file in `CQRS/` (or `CQRS/MetaHarness/` for optimization).
3. Name: `{HandlerName}Tests.cs` or `{CommandName}ValidatorTests.cs`.
4. Skeleton:

```csharp
using Application.Core.CQRS.Agents.ExecuteAgentTurn;
using Application.Core.Tests.Helpers;
using FluentAssertions;
using Moq;
using Xunit;

namespace Application.Core.Tests.CQRS;

public class NewHandlerTests
{
    private readonly Mock<IDependency> _dep = new();

    [Fact]
    public async Task Handle_Scenario_ExpectedResult()
    {
        // Arrange
        var handler = new NewHandler(_dep.Object);
        var command = new NewCommand { ... };

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
    }
}
```

5. Run: `dotnet test --filter "FullyQualifiedName~NewHandlerTests"`

## Shared Helpers and Fixtures

| Helper | Location | Purpose |
|--------|----------|---------|
| `TestableAIAgent` | `Helpers/TestableAIAgent.cs` | Configurable AIAgent subclass — fixed response, callback, or throws |
| `TestableAgentSession` | `Helpers/TestableAgentSession.cs` | Minimal AgentSession for handler tests |

## Dependencies

| Package | Purpose |
|---------|---------|
| xunit | Test framework |
| Moq | Mocking |
| FluentAssertions | Fluent assertion syntax |
| coverlet.collector | Code coverage |
| Microsoft.NET.Test.Sdk | VS Test platform |
| xunit.runner.visualstudio | IDE test discovery |
