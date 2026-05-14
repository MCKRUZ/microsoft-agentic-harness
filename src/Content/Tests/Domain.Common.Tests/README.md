# Domain.Common.Tests

## What This Tests

Unit and integration tests for the **Domain.Common** project — the shared domain layer containing the Result pattern, strongly-typed configuration hierarchy, workflow state machines, decision frameworks, input validation helpers, JSON utilities, logging models, telemetry source names, and MetaHarness domain models. Tests validate correctness of the foundational patterns that every other layer depends upon.

## Test Organization

Files are organized by domain concern: `Config/`, `Constants/`, `Enums/`, `Extensions/`, `Helpers/`, `Logging/`, `MetaHarness/`, `Middleware/`, `Models/`, `Workflow/`, `Telemetry/`, and `Integration/`. Root-level files cover core types like `Result`, `ResultExtensions`, and `StringExtensions`. Naming convention: `MethodName_Scenario_ExpectedResult`.

## All Test Classes

| Test Class | What It Tests | Approx Test Count | Unit/Integration |
|------------|---------------|-------------------|------------------|
| `ResultTests` | Result factory methods (Success, Fail, ValidationFailure, etc.) | 11 | Unit |
| `ResultGenericTests` | Result<T> typed success/failure | 5 | Unit |
| `ResultExtensionsTests` | Extension methods on Result | 4 | Unit |
| `ResultExtensionsLogicTests` | Complex Result chaining logic | 5 | Unit |
| `AIConfigTests` | AI configuration binding | 4 | Unit |
| `AppConfigTests` | Root AppConfig model | 4 | Unit |
| `McpConfigTests` | MCP configuration section | 3 | Unit |
| `ClaimConstantsTests` | Auth claim name constants | 2 | Unit |
| `PolicyNameConstantsTests` | Policy name string constants | 2 | Unit |
| `EnumExtensionsTests` | Enum extension utilities | 3 | Unit |
| `AuthPermissionsTests` | Auth permission enum values | 3 | Unit |
| `ResultFailureTypeTests` | Failure type enum completeness | 3 | Unit |
| `JsonAlphabetizerHelperTests` | JSON property alphabetical ordering | 4 | Unit |
| `JsonAlphabetizerHelperLogicTests` | Nested JSON alphabetization | 3 | Unit |
| `ResultHelperTests` | Result construction helpers | 4 | Unit |
| `ResultHelperLogicTests` | Complex result helper scenarios | 3 | Unit |
| `SecureInputValidatorHelperTests` | Input sanitization and injection prevention | 5 | Unit |
| `SecureInputValidatorHelperLogicTests` | Edge-case validation scenarios | 4 | Unit |
| `StringExtensionsTests` | String manipulation extensions | 4 | Unit |
| `ExecutionScopeTests` | Execution scope model | 3 | Unit |
| `ExecutionScopeLogicTests` | Scope nesting and hierarchy | 3 | Unit |
| `FileLoggerOptionsTests` | File logger options model | 3 | Unit |
| `EvalTaskTests` | MetaHarness evaluation task model | 3 | Unit |
| `ExecutionTraceRecordTests` | Execution trace recording | 3 | Unit |
| `HarnessCandidateTests` | Harness candidate model | 3 | Unit |
| `HarnessScoresTests` | Score aggregation model | 3 | Unit |
| `HarnessSnapshotTests` | Configuration snapshot model | 3 | Unit |
| `RegressionCheckResultTests` | Regression check model | 3 | Unit |
| `RegressionSuiteTests` | Regression suite model | 3 | Unit |
| `TraceScopeTests` | Trace scope model | 3 | Unit |
| `TraceScopeLogicTests` | Trace scope nesting logic | 3 | Unit |
| `GlobalErrorHandlerOptionsTests` | Error handler config model | 3 | Unit |
| `AuditEntryTests` | Audit entry model | 3 | Unit |
| `EndpointHealthResultTests` | Health check result model | 3 | Unit |
| `LogEntryTests` | Log entry model | 3 | Unit |
| `RunManifestTests` | Run manifest model | 3 | Unit |
| `AppSourceNamesTests` | Telemetry source name constants | 3 | Unit |
| `DecisionFrameworkTests` | Rule-based decision engine | 5 | Unit |
| `DecisionFrameworkLogicTests` | Multi-rule evaluation logic | 4 | Unit |
| `DecisionResultTests` | Decision result model | 3 | Unit |
| `DecisionResultLogicTests` | Result interpretation logic | 3 | Unit |
| `DecisionRuleTests` | Individual rule definition | 3 | Unit |
| `NodeStateTests` | Workflow node state model | 3 | Unit |
| `NodeStateLogicTests` | Node state transitions | 4 | Unit |
| `StateConfigurationTests` | State machine configuration | 3 | Unit |
| `WorkflowExceptionTests` | Workflow exception types | 3 | Unit |
| `WorkflowStateTests` | Workflow state model | 4 | Unit |
| `WorkflowStateLogicTests` | State transition guard logic | 4 | Unit |
| `DecisionFrameworkIntegrationTests` | Full decision pipeline | 3 | Integration |
| `HelperIntegrationTests` | Helper cross-interaction | 3 | Integration |
| `ResultChainIntegrationTests` | Result chaining across operations | 3 | Integration |
| `WorkflowStateMachineIntegrationTests` | Complete state machine flow | 4 | Integration |
| `_SmokeTests` | Assembly load verification | 2 | Unit |

## Testing Patterns and Example

Domain tests are pure unit tests. The Result pattern tests validate factory methods and failure type semantics.

```csharp
[Fact]
public void Fail_CreatesFailureResult()
{
    // Act — create a failed result
    var result = Result.Fail("something went wrong");

    // Assert — verify failure state, type, and error messages
    result.IsSuccess.Should().BeFalse();
    result.FailureType.Should().Be(ResultFailureType.General);
    result.Errors.Should().ContainSingle().Which.Should().Be("something went wrong");
}
```

**Pattern**: No mocking — domain models are pure. Tests validate factory methods (e.g., `Result.Success()`, `Result.Forbidden()`), computed properties, and collection/enum completeness. Integration tests validate multi-step workflows and Result chaining.

## How to Run

```bash
# All tests in this project
dotnet test src/Content/Tests/Domain.Common.Tests/Domain.Common.Tests.csproj

# With coverage
dotnet test src/Content/Tests/Domain.Common.Tests/Domain.Common.Tests.csproj --collect:"XPlat Code Coverage"

# Single class
dotnet test --filter "FullyQualifiedName~ResultTests"

# Single test
dotnet test --filter "FullyQualifiedName~ResultTests.Fail_CreatesFailureResult"
```

## How to Add a New Test

1. Identify the domain type under `Domain.Common` to test.
2. Create a file in the matching subfolder (e.g., `Workflow/NewStateTests.cs`).
3. Name: `{DomainTypeName}Tests.cs`.
4. Skeleton:

```csharp
using FluentAssertions;
using Xunit;

namespace Domain.Common.Tests.Workflow;

public class NewStateTests
{
    [Fact]
    public void FactoryMethod_ValidInput_ReturnsExpectedState()
    {
        var state = NewState.Create("input");

        state.IsValid.Should().BeTrue();
        state.Value.Should().Be("input");
    }

    [Fact]
    public void Default_Property_HasExpectedValue()
    {
        var state = new NewState();

        state.Status.Should().Be(Status.Pending);
    }
}
```

5. Run: `dotnet test --filter "FullyQualifiedName~NewStateTests"`

## Shared Helpers and Fixtures

None — domain tests create all instances inline. Integration tests use multi-step setup within the same test method.

## Dependencies

| Package | Purpose |
|---------|---------|
| xunit | Test framework |
| Moq | Mocking (available but unused in domain tests) |
| FluentAssertions | Fluent assertion syntax |
| coverlet.collector | Code coverage |
| Microsoft.NET.Test.Sdk | VS Test platform |
| xunit.runner.visualstudio | IDE test discovery |
