---
paths: src/Content/**/MCPServer/**/*.cs, src/Content/**/Tools/**/*.cs
---
# Tools & MCP Server Rules

## Tool Implementation Pattern
1. Define interface in `Application.AI.Common/Interfaces/` (e.g., `IMyTool`)
2. Implement in `Infrastructure.AI/Tools/` or `Infrastructure.Common/Services/`
3. Register as keyed singleton in `Infrastructure.Common/DependencyInjection.cs`
4. Add JSON schema via `IToolSchemaService` for agent discovery
5. Convert to `AITool` via `IToolConverter`

**A tool that needs the CALLING request's own scoped state (e.g. `IAgentExecutionContext`) stays
keyed SINGLETON, like every other tool — never `AddKeyedScoped`.** Every production caller resolves
a keyed `ITool` by name from a singleton holding the ROOT service provider
(`ToolChainBuilder.ResolveToolByName`, `FirstPartyToolLookup.Resolve`), so there is no per-request
scope at resolution time to register a scoped tool into — `AddKeyedScoped` throws
`InvalidOperationException` under `ValidateScopes = true` (every host) the moment either resolver
reaches it. This was tried and caught by the `correctness` gate before shipping (#521,
`ToolResultFetchTool`) — do not repeat it.

Instead, constructor-inject `IAmbientRequestScope` (itself a singleton, AsyncLocal-backed) and
resolve the scoped dependency from `IAmbientRequestScope.Current` inside the tool's `ExecuteAsync`,
per invocation — not at construction. This is the same pattern already used by a dozen-plus other
long-lived singletons that need per-request state (see `IAmbientRequestScope`'s own remarks, and
e.g. `KnowledgeMemoryContextProvider`, `EgressPolicyDelegatingHandler`). `ToolResultFetchTool` is
the reference example for a tool doing this. Contrast with `DocumentIngestTool`, which takes
`IServiceScopeFactory` to spawn its own INDEPENDENT fresh scope per call — the right shape when the
tool starts new work, not when it needs to read the calling request's own identity back.

## Tool Schema Requirements
Every tool must have:
- Clear name (snake_case): `"file_system"`, `"document_search"`
- Description suitable for LLM consumption (what it does, when to use it)
- JSON schema for parameters with types, descriptions, required fields
- Defined fallback behavior in ToolDeclaration

## MCP Server Setup
- ASP.NET Core WebAPI with `AddCustomMCPServer(appConfig)`
- Chain: `.WithHttpTransport()` → `.LoadTools()` → `.LoadPrompts()` → `.LoadResources()`
- JWT Bearer auth via Entra ID: validate issuer, audience, signing key
- Rate limiting policy: `RATE_LIMITER_AI_MCPSERVER_POLICY`
- Endpoints: `app.MapMcp().RequireAuthorization().RequireRateLimiting()`

## MCP Client Configuration
MCP servers defined in `appsettings.json` under `McpServers.Servers`:
```json
{
  "tool_name": {
    "Enabled": true,
    "Type": "Stdio|Http",
    "Command": "npx",
    "Args": ["@package/server-name", "path"],
    "StartupTimeoutSeconds": 30
  }
}
```
Never hardcode MCP server URLs. Use config binding via `AppConfig`.

## Agentic Harness Design Goals
Model after Claude Code's approach:
- Tools are discoverable at runtime, not compile-time only
- Skills declare which tools they need — agent resolves on demand
- MCP extends built-in tools with external capabilities
- Progressive disclosure keeps context budget manageable
- Tool execution is observable (OpenTelemetry spans per invocation)
