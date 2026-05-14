# Presentation.Common.Tests

Unit and integration tests for the **Presentation.Common** layer — shared presentation extensions, configuration helpers, exception filters, and security utilities.

## Framework

- **xUnit** — test framework
- **Moq** — mocking
- **FluentAssertions** — assertion library
- **Microsoft.AspNetCore.Mvc.Testing** — WebApplicationFactory integration tests
- **coverlet** — code coverage

## Key Test Classes

| Test Class | What It Tests |
|------------|---------------|
| `AppConfigHelperTests` | AppConfig binding and validation helpers |
| `ConfigurationHelperTests` | Configuration builder extension methods |
| `IServiceCollectionExtensionsTests` | Shared DI registration extensions |
| `ExceptionContextExtensionsTests` | MVC exception filter context mapping |
| `SystemUserTests` | System-level user identity for background ops |

## Running Tests

```bash
dotnet test src/Content/Tests/Presentation.Common.Tests
```

## Coverage

```bash
dotnet test src/Content/Tests/Presentation.Common.Tests --collect:"XPlat Code Coverage"
```
