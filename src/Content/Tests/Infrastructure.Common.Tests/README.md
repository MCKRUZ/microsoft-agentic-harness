# Infrastructure.Common.Tests

Unit tests for the **Infrastructure.Common** layer — shared middleware (CORS, security headers, exception handling), endpoint filters, identity services, and claim extensions.

## Framework

- **xUnit** — test framework
- **Moq** — mocking
- **FluentAssertions** — assertion library
- **coverlet** — code coverage

## Key Test Classes

| Test Class | What It Tests |
|------------|---------------|
| `GlobalExceptionMiddlewareTests` | Unhandled exception capture and ProblemDetails response |
| `SecurityHeadersMiddlewareTests` | HTTP security header injection |
| `DynamicCorsMiddlewareTests` | Dynamic CORS origin validation |
| `HttpAuthEndpointFilterTests` | Authentication endpoint filter |
| `IdentityServiceTests` | Current user identity resolution |

## Running Tests

```bash
dotnet test src/Content/Tests/Infrastructure.Common.Tests
```

## Coverage

```bash
dotnet test src/Content/Tests/Infrastructure.Common.Tests --collect:"XPlat Code Coverage"
```
