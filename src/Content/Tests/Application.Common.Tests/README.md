# Application.Common.Tests

## What This Tests

Unit tests for the **Application.Common** project — the shared application layer containing MediatR pipeline behaviors (caching, validation, authorization, timeout, tracing), logging infrastructure (file loggers, ring buffers, structured JSON, named pipes), and utility helpers (YAML frontmatter parsing, embedded resources, cache options). These tests ensure cross-cutting concerns work correctly independent of any AI-specific logic.

## Test Organization

Files are organized by concern area: `MediatRBehaviors/`, `Logging/`, `Helpers/`, `Exceptions/`. Each test class maps 1:1 to a production class. Naming convention: `MethodName_Scenario_ExpectedResult`.

## All Test Classes

| Test Class | What It Tests | Approx Test Count | Unit/Integration |
|------------|---------------|-------------------|------------------|
| `CachingBehaviorTests` | HybridCache integration: miss/hit, invalidation | 4 | Unit |
| `AuthorizationBehaviorTests` | Claims-based authorization enforcement | 4 | Unit |
| `RequestTracingBehaviorTests` | Distributed tracing propagation through pipeline | 3 | Unit |
| `RequestValidationBehaviorTests` | FluentValidation pipeline integration | 4 | Unit |
| `TimeoutBehaviorTests` | Request timeout enforcement | 3 | Unit |
| `CallbackLoggerProviderTests` | Callback-based logger creation and dispatch | 4 | Unit |
| `ExecutionScopeProviderTests` | Execution scope lifecycle management | 3 | Unit |
| `FileLoggerProviderTests` | File-based structured log writing | 4 | Unit |
| `InMemoryRingBufferLoggerProviderTests` | Bounded ring buffer log retention | 4 | Unit |
| `LogEntryFactoryTests` | LogEntry construction from log events | 3 | Unit |
| `LoggingHelperTests` | Logging utility methods | 3 | Unit |
| `NamedPipeLoggerProviderTests` | Named pipe transport for real-time log streaming | 4 | Unit |
| `StructuredJsonLoggerProviderTests` | JSON-formatted structured logging output | 4 | Unit |
| `CacheOptionsHelperTests` | Cache entry options factory | 3 | Unit |
| `EmbeddedResourceHelperTests` | Assembly embedded resource extraction | 3 | Unit |
| `YamlFrontmatterHelperTests` | YAML frontmatter parsing from markdown | 4 | Unit |
| `ExceptionTypesTests` | Custom exception hierarchy validation | 4 | Unit |
| `_SmokeTests` | Assembly load and basic wiring | 2 | Unit |

## Testing Patterns and Example

Tests use real HybridCache instances (from `ServiceCollection`) for caching tests and Moq for simpler behaviors. MediatR delegate pattern (`RequestHandlerDelegate<T>`) simulates the next handler in the pipeline.

```csharp
[Fact]
public async Task Handle_CacheableQuery_CacheMiss_CallsHandlerAndStoresResult()
{
    // Arrange — create behavior with real HybridCache
    var behavior = new CachingBehavior<TestQuery, string>(
        _cache,
        NullLogger<CachingBehavior<TestQuery, string>>.Instance);

    var callCount = 0;
    RequestHandlerDelegate<string> next = () =>
    {
        callCount++;
        return Task.FromResult("computed-value");
    };

    // Act — call twice with same cache key
    var result1 = await behavior.Handle(
        new TestQuery("key-1"), next, CancellationToken.None);
    var result2 = await behavior.Handle(
        new TestQuery("key-1"), next, CancellationToken.None);

    // Assert — handler called once, both results correct
    result1.Should().Be("computed-value");
    result2.Should().Be("computed-value");
    callCount.Should().Be(1, "second call should use cached value");
}
```

**Mocking pattern**: Real in-memory services (`MemoryCache`, `HybridCache`) for caching tests. `NullLogger<T>.Instance` for logging. Inner request types (`TestQuery`, `TestCommand`, `PlainRequest`) defined as private records inside each test class.

## How to Run

```bash
# All tests in this project
dotnet test src/Content/Tests/Application.Common.Tests/Application.Common.Tests.csproj

# With coverage
dotnet test src/Content/Tests/Application.Common.Tests/Application.Common.Tests.csproj --collect:"XPlat Code Coverage"

# Single class
dotnet test --filter "FullyQualifiedName~CachingBehaviorTests"

# Single test
dotnet test --filter "FullyQualifiedName~CachingBehaviorTests.Handle_CacheableQuery_CacheMiss_CallsHandlerAndStoresResult"
```

## How to Add a New Test

1. Identify the production class under `Application.Common`.
2. Create a file in the matching subfolder (e.g., `MediatRBehaviors/NewBehaviorTests.cs`).
3. Name convention: `{ProductionClassName}Tests.cs`.
4. Skeleton:

```csharp
using FluentAssertions;
using MediatR;
using Moq;
using Xunit;

namespace Application.Common.Tests.MediatRBehaviors;

public class NewBehaviorTests
{
    private record TestRequest : IRequest<string>;

    private static RequestHandlerDelegate<T> NextReturning<T>(T value) =>
        () => Task.FromResult(value);

    [Fact]
    public async Task Handle_Scenario_ExpectedResult()
    {
        // Arrange
        var behavior = new NewBehavior<TestRequest, string>(...);

        // Act
        var result = await behavior.Handle(new TestRequest(), NextReturning("value"), CancellationToken.None);

        // Assert
        result.Should().Be("value");
    }
}
```

5. Run tests to verify: `dotnet test --filter "FullyQualifiedName~NewBehaviorTests"`

## Shared Helpers and Fixtures

No shared fixtures — each test class is self-contained. Caching tests use `IDisposable` to clean up `ServiceProvider` and `MemoryCache` instances.

## Dependencies

| Package | Purpose |
|---------|---------|
| xunit | Test framework |
| Moq | Mocking |
| FluentAssertions | Fluent assertion syntax |
| coverlet.collector | Code coverage |
| Microsoft.NET.Test.Sdk | VS Test platform |
| xunit.runner.visualstudio | IDE test discovery |
| Microsoft.Extensions.Caching.Hybrid | Real HybridCache for integration-like tests |
| Microsoft.Extensions.Caching.Memory | In-memory cache backend |
| Microsoft.Extensions.Logging.Abstractions | NullLogger usage |
| Microsoft.Extensions.Options | IOptions pattern support |
