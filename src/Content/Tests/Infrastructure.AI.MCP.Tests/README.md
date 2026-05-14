# Infrastructure.AI.MCP.Tests

Unit and integration tests for the **Infrastructure.AI.MCP** layer — MCP client connection management, tool provider discovery, and trace resource providers.

## Framework

- **xUnit** — test framework
- **Moq** — mocking
- **FluentAssertions** — assertion library
- **coverlet** — code coverage

## Key Test Classes

| Test Class | What It Tests |
|------------|---------------|
| `McpConnectionManagerTests` | MCP server connection lifecycle management |
| `McpToolProviderTests` | Dynamic tool discovery from MCP servers |
| `McpToolProviderIntegrationTests` | End-to-end MCP tool resolution |
| `TraceResourceProviderTests` | MCP resource provider for execution traces |
| `DependencyInjectionTests` | Service registration verification |

## Running Tests

```bash
dotnet test src/Content/Tests/Infrastructure.AI.MCP.Tests
```

## Coverage

```bash
dotnet test src/Content/Tests/Infrastructure.AI.MCP.Tests --collect:"XPlat Code Coverage"
```
