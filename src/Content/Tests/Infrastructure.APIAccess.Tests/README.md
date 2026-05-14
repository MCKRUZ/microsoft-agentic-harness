# Infrastructure.APIAccess.Tests

Unit and integration tests for the **Infrastructure.APIAccess** layer — HTTP handler pipeline, permission-based authorization, endpoint resolution, and delegating handlers.

## Framework

- **xUnit** — test framework
- **Moq** — mocking
- **FluentAssertions** — assertion library
- **coverlet** — code coverage

## Key Test Classes

| Test Class | What It Tests |
|------------|---------------|
| `PermissionPolicyProviderTests` | Dynamic authorization policy generation |
| `PermissionAuthHandlerTests` | Claims-based permission authorization handler |
| `CorrelationIdDelegatingHandlerTests` | Correlation ID propagation across HTTP calls |
| `ApiEndpointResolverServiceTests` | API endpoint discovery and resolution |
| `HttpHandlerPipelineTests` | Full delegating handler chain composition |

## Running Tests

```bash
dotnet test src/Content/Tests/Infrastructure.APIAccess.Tests
```

## Coverage

```bash
dotnet test src/Content/Tests/Infrastructure.APIAccess.Tests --collect:"XPlat Code Coverage"
```
