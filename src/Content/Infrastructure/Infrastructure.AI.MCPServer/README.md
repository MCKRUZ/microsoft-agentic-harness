# Infrastructure.AI.MCPServer

## What This Project Is

Infrastructure.AI.MCPServer is a standalone ASP.NET Core web application that exposes the harness's capabilities to the outside world via the MCP (Model Context Protocol). While Infrastructure.AI.MCP is the client (consuming external tools), this project is the server (providing tools to external consumers). External agents, IDEs like Claude Desktop, automation pipelines, and other MCP-compliant clients can connect and use the harness's skill catalog, tools, prompts, and resources through the standardized MCP protocol.

The problem it solves: the harness contains valuable capabilities (skills, tools, knowledge) that should be accessible to any MCP-compatible system. Without a server, these capabilities are locked inside the harness process. The MCP server makes the harness a first-class participant in the broader MCP ecosystem.

This is a web application project (Microsoft.NET.Sdk.Web) that runs as its own process. It depends on Application.AI.Common and Application.Common (for interfaces), Domain.Common (for configuration), and Infrastructure.AI (for skill registry and tool implementations). Presentation hosts can also embed this server as a sub-application.

**Analogy:** If Infrastructure.AI.MCP is a web browser (consuming content from servers), this project is the web server (serving content to browsers). Together they make the harness both a consumer and provider of MCP capabilities.

## Architecture Context

```
External MCP Clients
  (Claude Desktop, VS Code, other agents)
       |
       | HTTP + MCP Protocol (JSON-RPC over streamable HTTP)
       v
+-----------------------------------------------+
|       Infrastructure.AI.MCPServer              |
|                                                |
|  ASP.NET Core Pipeline:                        |
|    Authentication (ApiKey / Bearer / Entra)    |
|    Authorization                               |
|    Rate Limiting (fixed window)                |
|    MCP Endpoint (/mcp)                         |
|                                                |
|  Tools:                                        |
|    SkillTools (list_skills, get_skill,          |
|               find_skills_by_tag)              |
|    + tools loaded from configured assemblies   |
|                                                |
|  Extensions:                                   |
|    McpServerExtensions (server setup)          |
|    McpServerBuilderExtensions (assembly scan)  |
+-----------------------------------------------+
         |
         v
  Infrastructure.AI (SkillMetadataRegistry)
  Application.AI.Common (interfaces)
```

## Key Concepts

### The MCP Server Application

**What it is:** A minimal ASP.NET Core WebAPI that hosts the MCP protocol endpoint.

**Why it exists:** MCP requires an HTTP server that speaks the MCP JSON-RPC protocol. This project provides that server with enterprise features: JWT authentication, rate limiting, and extensible tool/prompt/resource loading.

**How the startup works (Program.cs):**
1. Bind `AppConfig` from configuration.
2. Register MCP server services with HTTP transport.
3. Configure authentication **fail-closed**: ApiKey, static Bearer token, or Entra ID (JWT). If no scheme is properly configured and `Auth.AllowAnonymous` is not explicitly `true`, the host throws at startup — in every environment, including Development.
4. Register the skill catalog (`SkillMetadataRegistry`) for tool queries.
5. Add rate limiting (100 requests/minute per client).
6. Build the pipeline: Authentication -> Authorization -> Rate Limiter -> MCP endpoint.
7. Map the MCP endpoint at the root with auth + rate limiting required (or a deliberate `.AllowAnonymous()` when the opt-in is set).

```csharp
var mcpEndpoints = app.MapMcp().RequireRateLimiting("mcp");
if (appConfig.AI.MCP.Auth.AllowAnonymous)
    mcpEndpoints.AllowAnonymous();
else
    mcpEndpoints.RequireAuthorization();
```

### SkillTools (Built-in MCP Tools)

**What it is:** A set of MCP tools that expose the harness's skill catalog to external clients.

**Why it exists:** External agents connecting via MCP need to discover what skills this harness offers. These tools provide a searchable, filterable interface to the skill metadata.

**Three tools exposed:**

| Tool | Purpose | Parameters |
|------|---------|-----------|
| `list_skills` | List all skills, optionally filtered by category | `category?` (string) |
| `get_skill` | Get full details + instructions for one skill | `skillId` (string) |
| `find_skills_by_tag` | Search skills by tags | `tags` (comma-separated string) |

**Example interaction from an external MCP client:**
```json
// Request: list_skills
{"category": "research"}

// Response:
[
  {"id": "research-agent", "name": "Research Agent", "description": "...", "category": "research", "tags": ["web", "analysis"]}
]

// Request: get_skill
{"skillId": "research-agent"}

// Response:
{"id": "research-agent", ..., "instructions": "You are a research specialist...", "allowedTools": ["web_search", "document_search"]}
```

### Assembly-Based Tool Loading

**What it is:** A mechanism to discover and load MCP tools, prompts, and resources from external assemblies at startup.

**Why it exists:** The MCP server should be extensible without modifying its source code. By configuring assembly names in `McpConfig.ScanAssemblies`, new tools can be added by dropping a DLL and updating config.

**How it works:**
- `McpServerBuilderExtensions` provides three extension methods: `LoadToolsFromAssemblies()`, `LoadPromptsFromAssemblies()`, `LoadResourcesFromAssemblies()`.
- Each loads assemblies by name via `Assembly.Load()` and registers any types decorated with `[McpServerToolType]`, `[McpServerPromptType]`, or `[McpServerResourceType]`.
- The built-in assembly (containing `SkillTools`) is always loaded first.

### Authentication (fail-closed)

**What it is:** Mandatory authentication on every MCP endpoint, supporting three schemes — API key header, static Bearer token, and Entra ID JWT — with a fail-closed startup contract.

**Why it exists:** MCP servers must never silently end up open. If authentication is absent or misconfigured, the host refuses to start with a clear error instead of booting anonymous. The environment name is never an implicit bypass.

**How it works:**
- `Auth.Type == ApiKey`: inbound requests must present the shared key in the configured header (default `X-API-Key`). Compared in constant time.
- `Auth.Type == Bearer`: inbound requests must present the shared token as `Authorization: Bearer {token}`. Compared in constant time.
- `Auth.Type == Entra`: JWT Bearer validation —
  - Authority: `https://login.microsoftonline.com/{TenantId}/v2.0`
  - Audience: `api://{ClientId}`
  - Token validation: issuer, audience, lifetime, signing key, zero clock skew
- `Auth.Type == None` and `Auth.AllowAnonymous == false` (the default): the host **throws at startup** in every environment.
- `Auth.Type == None` and `Auth.AllowAnonymous == true`: the explicit local-development opt-in. The server serves anonymously and logs a prominent warning at startup. Combining `AllowAnonymous=true` with a configured type is rejected as contradictory.
- A configured type missing its credential material (e.g. `ApiKey` with no key, `Entra` with no `TenantId`) also fails at startup.
- When authentication is configured, a fallback authorization policy (`RequireAuthenticatedUser`) covers any endpoint mapped without explicit authorization metadata, and a per-tool-call gate (`McpToolAuthorizationFilter`) re-checks the principal at the dispatch layer as defense-in-depth.

### Rate Limiting

**What it is:** A fixed-window rate limiter that caps MCP requests at 100 per minute.

**Why it exists:** MCP tools can trigger expensive operations (LLM calls, database queries). Rate limiting prevents abuse and protects downstream resources.

### Resource Subscriptions

**What it is:** Handlers for MCP's resource subscription protocol (subscribe/unsubscribe/notification).

**Why it exists:** MCP clients can subscribe to resource changes and receive notifications when resources update. The server maintains a `ConcurrentDictionary` of active subscriptions.

## Data Flow

```
External MCP Client (e.g., Claude Desktop)
       |
       | POST /mcp (JSON-RPC request)
       v
[ASP.NET Core Pipeline]
  1. JWT Bearer Authentication (validate token)
  2. Authorization (check claims)
  3. Rate Limiter (100 req/min check)
       |
       v
[MCP Protocol Handler]
  - tools/list --> enumerate all registered tool types
  - tools/call --> dispatch to tool method (e.g., SkillTools.ListSkills)
  - resources/list --> enumerate resource providers
  - resources/read --> read specific resource content
  - prompts/list --> enumerate prompt types
  - logging/setLevel --> adjust server log verbosity
       |
       v
[Tool Implementation (e.g., SkillTools)]
  - Resolves ISkillMetadataRegistry from DI
  - Queries skill catalog
  - Returns JSON response
       |
       v
[JSON-RPC response back to client]
```

## Project Structure

```
Infrastructure.AI.MCPServer/
├── Authentication/
│   ├── McpSharedKeyAuthenticationDefaults.cs   Scheme name constants (ApiKey / shared Bearer)
│   ├── McpSharedKeyAuthenticationOptions.cs    Header, prefix, expected credential + validation
│   ├── McpSharedKeyAuthenticationHandler.cs    Constant-time shared-secret authentication
│   └── McpAnonymousModeStartupWarning.cs       Prominent warning while AllowAnonymous=true
├── Authorization/
│   └── McpToolAuthorizationFilter.cs   Per-tool-call gate (defense-in-depth)
├── Extensions/
│   ├── McpServerExtensions.cs          Server setup, fail-closed auth, subscription handlers
│   └── McpServerBuilderExtensions.cs   Assembly scanning for tools/prompts/resources
├── Tools/
│   └── SkillTools.cs                   list_skills, get_skill, find_skills_by_tag
├── Program.cs                          Entry point (WebApplication builder + pipeline)
├── appsettings.json                    Server configuration
├── appsettings.Development.json        Dev overrides
└── Infrastructure.AI.MCPServer.csproj  SDK.Web project file
```

## Key Types Reference

| Type | Purpose | Lifetime |
|------|---------|----------|
| `Program` | Application entry point and pipeline setup | -- |
| `SkillTools` | MCP tools for skill catalog queries | Transient (per-request) |
| `McpServerExtensions` | Server registration + auth + subscriptions | -- (static) |
| `McpServerBuilderExtensions` | Assembly scanning helpers | -- (static) |

## Configuration

```jsonc
{
  "AppConfig": {
    "AI": {
      "MCP": {
        "ServerName": "agentic-harness",           // Reported in MCP handshake
        "ServerVersion": "1.0.0",                  // Server version
        "ServerInstructions": "Agent harness MCP server providing skills and tools.",
        "InitializationTimeout": "00:00:30",       // Handshake timeout
        "ScanAssemblies": [                        // Additional assemblies to load tools from
          "MyCustomTools.Assembly"
        ],
        "Auth": {
          "Type": "Entra",                         // None | ApiKey | Bearer | Entra
          "AllowAnonymous": false,                 // Explicit dev-only opt-in; default false (fail-closed)
          "TenantId": "xxxxxxxx-xxxx-...",         // Entra: Azure AD tenant
          "ClientId": "yyyyyyyy-yyyy-...",         // Entra: app registration client ID
          "ApiKeyHeader": "X-API-Key"              // ApiKey: header name (key itself via User Secrets/Key Vault)
          // "ApiKey" / "BearerToken": supply via User Secrets or Key Vault — never appsettings.json
        }
      }
    }
  }
}
```

### Security Configuration

| Setting | Purpose | Recommendation |
|---------|---------|---------------|
| `Auth.Type` | Selects the enforcement scheme (`ApiKey`, `Bearer`, `Entra`) | `Entra` in production; `None` refuses to start unless `AllowAnonymous=true` |
| `Auth.AllowAnonymous` | Explicit opt-in to serve without authentication | Never in production; logs a prominent startup warning while on |
| `Auth.ApiKey` / `Auth.BearerToken` | Shared credential inbound requests must present | User Secrets or Key Vault only |
| `Auth.ApiKeyHeader` | Header carrying the API key | Default `X-API-Key` |
| `Auth.TenantId` | Entra tenant for token validation | Your organization's tenant |
| `Auth.ClientId` | App registration for audience validation | Dedicated app registration |
| Rate limit | 100 requests per minute per partition | Tune based on expected load |

## Common Tasks

### How to Add a New MCP Tool

1. Create a class decorated with `[McpServerToolType]`:
```csharp
[McpServerToolType]
public sealed class MyTools(IMyService service)
{
    [McpServerTool(Name = "my_tool_name")]
    [Description("What this tool does and when to use it.")]
    public string DoSomething(
        [Description("Parameter description for LLM.")]
        string input)
    {
        var result = service.Process(input);
        return JsonSerializer.Serialize(result);
    }
}
```

2. If in the same assembly: it's auto-discovered via `WithToolsFromAssembly()`.
3. If in a different assembly: add the assembly name to `McpConfig.ScanAssemblies`.
4. Register any DI dependencies the tool class needs.

### How to Run the MCP Server Standalone

```bash
dotnet run --project src/Content/Infrastructure/Infrastructure.AI.MCPServer
```

The server starts on the configured URLs (default: `http://localhost:5000`). Connect any MCP client to `http://localhost:5000/mcp`.

### How to Test MCP Tools Locally

Use the MCP Inspector or Claude Desktop:
```bash
npx @anthropic/mcp-inspector http://localhost:5000/mcp
```

Or configure Claude Desktop's `claude_desktop_config.json`:
```json
{
  "mcpServers": {
    "harness": {
      "url": "http://localhost:5000/mcp"
    }
  }
}
```

### How to Debug Authentication Issues

1. Verify `Auth.TenantId` and `Auth.ClientId` match your Entra app registration.
2. Check that the token's `aud` claim matches `api://{ClientId}`.
3. Check that the token's `iss` claim matches one of the two valid issuer patterns.
4. If the host refuses to start with a fail-closed error, that is intentional: configure `Auth.Type` with its credential material, or — for local development only — consciously set `Auth.AllowAnonymous = true` (a warning is logged at startup while it is on). Running under `ASPNETCORE_ENVIRONMENT=Development` does not bypass authentication.

## Dependencies

**Project References:**
- `Application.AI.Common` -- `ISkillMetadataRegistry` and related interfaces
- `Application.Common` -- Pipeline behaviors, logging
- `Domain.Common` -- `AppConfig`, `McpConfig` configuration models
- `Infrastructure.AI` -- `SkillMetadataParser`, `SkillMetadataRegistry` implementations

**NuGet Packages:**
- `ModelContextProtocol.AspNetCore` -- ASP.NET Core hosting for MCP servers (HTTP transport, endpoint mapping)
- `Microsoft.AspNetCore.Authentication.JwtBearer` -- JWT token validation for Entra ID

## Testing

- **Test project:** `Infrastructure.AI.MCPServer.Tests`
- **Run:** `dotnet test --filter "FullyQualifiedName~Infrastructure.AI.MCPServer.Tests"`
- **Mock guidance:** Use `WebApplicationFactory<Program>` for integration tests. Mock `ISkillMetadataRegistry` to provide test skill data. For auth testing, generate test JWTs with matching issuer/audience claims. The rate limiter can be tested by sending rapid bursts and verifying 429 responses.
