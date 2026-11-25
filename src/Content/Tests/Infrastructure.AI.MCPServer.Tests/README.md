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
| `McpServerProtocolVersionTests` | Full-host HTTP checks per protocol revision: a 2026-07-28 tool call with no handshake, a 2025-11-25 handshake and `tools/list`, no advertised resource subscriptions, `resources/subscribe`/`unsubscribe` and 2026-07-28 `logging/setLevel` refused with `-32601`, and an empty `resources/list` |

## Running Tests

```bash
dotnet test src/Content/Tests/Infrastructure.AI.MCPServer.Tests
```

## Coverage

```bash
dotnet test src/Content/Tests/Infrastructure.AI.MCPServer.Tests --collect:"XPlat Code Coverage"
```
