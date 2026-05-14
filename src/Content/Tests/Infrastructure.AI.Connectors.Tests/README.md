# Infrastructure.AI.Connectors.Tests

## What This Tests

Unit tests for the **Infrastructure.AI.Connectors** project — external service connector implementations for GitHub Issues, GitHub Repos, Azure DevOps Work Items, and Jira Issues. Tests validate connector availability detection, parameter validation, HTTP response handling (success, errors, network failures), and the connector factory/base patterns. All external HTTP calls are mocked via `HttpMessageHandler`.

## Test Organization

Files are organized by connector provider: `GitHub/`, `AzureDevOps/`, `Jira/`, and `Core/` for shared base/factory. Each connector test class covers properties, validation, and all supported operations. Naming convention: `MethodName_Scenario_ExpectedResult`.

## All Test Classes

| Test Class | What It Tests | Approx Test Count | Unit/Integration |
|------------|---------------|-------------------|------------------|
| `ConnectorClientBaseTests` | Base HTTP client behavior, auth headers, retry | 5 | Unit |
| `ConnectorClientFactoryTests` | Factory pattern for connector instantiation | 4 | Unit |
| `GitHubIssuesConnectorTests` | GitHub Issues: list, create, update, close | 26 | Unit |
| `GitHubReposConnectorTests` | GitHub Repos: list, search, metadata | 8 | Unit |
| `AzureDevOpsWorkItemsConnectorTests` | Azure DevOps: work item CRUD | 10 | Unit |
| `JiraIssuesConnectorTests` | Jira: issue list, create, update, transition | 10 | Unit |
| `_SmokeTests` | Assembly load verification | 2 | Unit |

## Testing Patterns and Example

Tests mock `HttpMessageHandler` using Moq.Protected to intercept `SendAsync`, then verify connector behavior against various HTTP status codes and response bodies.

```csharp
[Fact]
public async Task ListIssues_ValidParams_ReturnsSuccessWithIssues()
{
    // Arrange — set up mock HTTP response with JSON payload
    var connector = CreateConnector();
    var jsonResponse = JsonSerializer.Serialize(new[]
    {
        new { number = 1, title = "First issue", state = "open",
              user = new { login = "testuser" },
              labels = new[] { new { name = "bug" } },
              html_url = "https://github.com/test-owner/repo/issues/1",
              created_at = "2026-01-01T00:00:00Z" }
    });
    SetupHttpResponse(HttpStatusCode.OK, jsonResponse);

    // Act — execute the connector operation
    var parameters = new Dictionary<string, object> { ["repo"] = "my-repo" };
    var result = await connector.ExecuteAsync("list_issues", parameters);

    // Assert — verify success and content
    result.IsSuccess.Should().BeTrue();
    result.MarkdownResult.Should().Contain("First issue");
}
```

**Mocking pattern**: `Mock<HttpMessageHandler>` with `.Protected().Setup<Task<HttpResponseMessage>>("SendAsync", ...)` for HTTP interception. `Mock<IOptionsMonitor<AppConfig>>` for configuration. Helper methods `CreateConnector()`, `SetupHttpResponse()`, and `SetupHttpException()` encapsulate test infrastructure.

## How to Run

```bash
# All tests in this project
dotnet test src/Content/Tests/Infrastructure.AI.Connectors.Tests/Infrastructure.AI.Connectors.Tests.csproj

# With coverage
dotnet test src/Content/Tests/Infrastructure.AI.Connectors.Tests/Infrastructure.AI.Connectors.Tests.csproj --collect:"XPlat Code Coverage"

# Single class
dotnet test --filter "FullyQualifiedName~GitHubIssuesConnectorTests"

# Single test
dotnet test --filter "FullyQualifiedName~GitHubIssuesConnectorTests.ListIssues_ValidParams_ReturnsSuccessWithIssues"
```

## How to Add a New Test

1. Identify the connector under `Infrastructure.AI.Connectors`.
2. Create a file in the matching provider folder (e.g., `GitHub/GitHubPullsConnectorTests.cs`).
3. Follow the existing pattern with `#region` blocks per operation.
4. Skeleton:

```csharp
using System.Net;
using Domain.Common.Config;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using Xunit;

namespace Infrastructure.AI.Connectors.Tests.GitHub;

public class GitHubPullsConnectorTests
{
    private readonly Mock<HttpMessageHandler> _mockHandler = new();
    private readonly Mock<IOptionsMonitor<AppConfig>> _appConfigMonitor = new();

    private GitHubPullsConnector CreateConnector() { ... }

    private void SetupHttpResponse(HttpStatusCode statusCode, string content)
    {
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content)
            });
    }

    [Fact]
    public async Task ListPulls_ValidParams_ReturnsSuccess()
    {
        var connector = CreateConnector();
        SetupHttpResponse(HttpStatusCode.OK, "[]");

        var result = await connector.ExecuteAsync("list_pulls", new Dictionary<string, object> { ["repo"] = "r" });

        result.IsSuccess.Should().BeTrue();
    }
}
```

5. Run: `dotnet test --filter "FullyQualifiedName~GitHubPullsConnectorTests"`

## Shared Helpers and Fixtures

No shared fixtures. Each test class contains its own `CreateConnector()` and `SetupHttpResponse()` helper methods following a consistent pattern.

## Dependencies

| Package | Purpose |
|---------|---------|
| xunit | Test framework |
| Moq | Mocking (including Protected for HttpMessageHandler) |
| FluentAssertions | Fluent assertion syntax |
| coverlet.collector | Code coverage |
| Microsoft.NET.Test.Sdk | VS Test platform |
| xunit.runner.visualstudio | IDE test discovery |
