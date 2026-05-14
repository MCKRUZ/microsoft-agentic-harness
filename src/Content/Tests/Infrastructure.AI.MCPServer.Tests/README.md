# Infrastructure.AI.MCPServer.Tests

Unit tests for the **Infrastructure.AI.MCPServer** layer — MCP server hosting, builder extensions, and skill-based tool exposure.

## Framework

- **xUnit** — test framework
- **Moq** — mocking
- **FluentAssertions** — assertion library
- **coverlet** — code coverage

## Key Test Classes

| Test Class | What It Tests |
|------------|---------------|
| `SkillToolsTests` | Skill-to-MCP tool conversion and invocation |
| `SkillToolsEdgeCaseTests` | Edge cases in skill tool registration |
| `McpServerBuilderExtensionsTests` | MCP server builder DI extensions |
| `McpServerExtensionsTests` | MCP server endpoint configuration |

## Running Tests

```bash
dotnet test src/Content/Tests/Infrastructure.AI.MCPServer.Tests
```

## Coverage

```bash
dotnet test src/Content/Tests/Infrastructure.AI.MCPServer.Tests --collect:"XPlat Code Coverage"
```
