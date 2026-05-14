# Presentation.AgentHub.Tests

Unit and integration tests for the **Presentation.AgentHub** layer — ASP.NET Core API controllers, SignalR hubs, AG-UI streaming, conversation persistence, and telemetry export.

## Framework

- **xUnit** — test framework
- **Moq** — mocking
- **FluentAssertions** — assertion library
- **Microsoft.AspNetCore.Mvc.Testing** — WebApplicationFactory integration tests
- **Microsoft.AspNetCore.SignalR.Client** — SignalR hub testing
- **coverlet** — code coverage

## Key Test Classes

| Test Class | What It Tests |
|------------|---------------|
| `AgUiRunHandlerTests` | AG-UI SSE streaming orchestration |
| `AgentTelemetryHubTests` | SignalR telemetry hub message dispatch |
| `AgentsControllerTests` | Agent listing and metadata API endpoints |
| `FileSystemConversationStoreTests` | Conversation persistence CRUD |
| `CoreSetupTests` | Host startup and DI container validation |

## Running Tests

```bash
dotnet test src/Content/Tests/Presentation.AgentHub.Tests
```

## Coverage

```bash
dotnet test src/Content/Tests/Presentation.AgentHub.Tests --collect:"XPlat Code Coverage"
```
