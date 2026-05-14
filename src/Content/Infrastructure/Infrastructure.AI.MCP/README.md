# Infrastructure.AI.MCP

## What This Project Is

Infrastructure.AI.MCP is the MCP (Model Context Protocol) client that allows the agentic harness to consume tools from external MCP servers. MCP is a standard protocol (developed by Anthropic) that lets AI agents discover and invoke tools hosted by separate processes or remote services. This project manages connections to those external servers, discovers their available tools, and makes them available to agents as if they were built-in.

The problem it solves: AI agents are more useful when they can access tools beyond what's compiled into the harness -- code search engines, database query interfaces, deployment pipelines, documentation servers, or any specialist service. Without MCP client support, each integration would require custom code. With MCP, any compliant server is automatically consumable.

This project depends on Application.AI.Common (for `IMcpToolProvider` and `IMcpResourceProvider` interfaces) and uses the `ModelContextProtocol` NuGet package for protocol implementation. It is referenced by Presentation hosts that register the DI extensions.

**Analogy:** If the agent is a developer, the MCP client is like their package manager -- it connects to external registries (MCP servers), discovers what's available, and makes those capabilities usable in the local environment.

## Architecture Context

```
External MCP Servers                Application.AI.Common
  (stdio, HTTP, SSE)                   IMcpToolProvider
       |                               IMcpResourceProvider
       v                                    |
+-----------------------------------------------+
|           Infrastructure.AI.MCP                |
|                                                |
|  McpConnectionManager (lifecycle management)   |
|       |                                        |
|       v                                        |
|  McpToolProvider (tool discovery + caching)     |
|  TraceResourceProvider (trace:// resources)    |
+-----------------------------------------------+
         ^
         |
  services.AddMcpClientDependencies();
```

The connection manager handles three transport types:
- **Stdio:** Spawns a child process and communicates via stdin/stdout (local tools like `npx @tool/server`)
- **HTTP:** Connects to a remote HTTP endpoint using the Streamable HTTP transport
- **SSE (Server-Sent Events):** Legacy SSE-based transport for older MCP servers

## Key Concepts

### McpConnectionManager

**What it is:** A singleton that manages the complete lifecycle of MCP client connections -- creation, caching, health checking, and disposal.

**Why it exists:** MCP connections are expensive to establish (process spawn, handshake, capability negotiation). Connections must be reused across multiple tool invocations within a session, and cleaned up when the application shuts down. The manager handles thread-safe lazy initialization and graceful teardown.

**How it works:**
1. On first access for a server name, acquires a per-server semaphore (prevents duplicate connections).
2. Looks up the server definition from `McpServersConfig.Servers[name]`.
3. Creates the appropriate transport (Stdio, HTTP, or SSE) from the definition.
4. Calls `McpClient.CreateAsync()` with configured timeout and client metadata.
5. Caches the connected client in a `ConcurrentDictionary`.
6. Returns the cached client on subsequent calls.
7. Implements `IAsyncDisposable` to close all connections on shutdown.

**Error handling:** Failed connections throw `McpConnectionException` with the server name and transport type, enabling structured error reporting. Disabled servers are explicitly rejected.

```csharp
// Internal usage by McpToolProvider:
var client = await _connectionManager.GetClientAsync("filesystem-server", ct);
var tools = await client.ListToolsAsync(ct);
```

### McpToolProvider

**What it is:** The public-facing service that agents use to discover and invoke MCP tools.

**Why it exists:** The connection manager deals in raw MCP clients. The tool provider translates MCP's tool list into `AITool` instances that the Microsoft.Extensions.AI framework understands, handles failures gracefully (skipping unavailable servers rather than crashing), and provides convenience methods for multi-server discovery.

**How it works:**
1. `GetToolsAsync(serverName)` -- connects to one server, lists its tools, returns as `IList<AITool>`.
2. `GetAllToolsAsync()` -- connects to ALL configured/enabled servers in parallel, returns a dictionary of server name to tool list. Unavailable servers are logged and skipped.
3. `GetToolByNameAsync(name)` -- searches all servers for a specific tool by name (used for on-demand resolution).
4. `IsServerAvailableAsync(serverName)` -- health check (can we connect?).

**Observability:** Every tool discovery operation records OpenTelemetry metrics via `McpServerMetrics`:
- `McpServerMetrics.Requests` (counter with server name, operation, status tags)
- `McpServerMetrics.RequestDuration` (histogram of connection + listing time)

```csharp
// Used by the agent orchestration layer:
var allTools = await _mcpToolProvider.GetAllToolsAsync(ct);
// Returns: { "filesystem": [read, write, search], "git": [status, commit, diff] }
```

### TraceResourceProvider

**What it is:** An MCP resource provider that exposes optimization run trace files via `trace://` URIs.

**Why it exists:** The MetaHarness optimization system generates trace files for each run. Exposing them as MCP resources allows external tools (dashboards, analysis scripts) to access trace data through the standard MCP resource protocol, with authentication and path traversal protection.

**How it works:**
- URI scheme: `trace://{optimizationRunId}/{relativePath}`
- Directory layout: `{TraceDirectoryRoot}/optimizations/{runId}/`
- Security: requires JWT authentication, rejects path traversal (`..`), validates symlinks on non-Windows, checks containment within run directory
- Feature-gated: controlled by `MetaHarnessConfig.EnableMcpTraceResources`

## Data Flow

```
Agent orchestration requests MCP tools
       |
       v
[McpToolProvider.GetAllToolsAsync()]
       |
       v
[For each configured server (parallel):]
  |
  v
[McpConnectionManager.GetClientAsync(serverName)]
  |
  v
[Cache hit?] --yes--> Return cached McpClient
  |no
  v
[Create transport (Stdio/HTTP/SSE)]
  |
  v
[McpClient.CreateAsync(transport, options)]
  |
  v
[client.ListToolsAsync()] --> AITool list
       |
       v
[Aggregate all tools from all servers]
       |
       v
[Return Dictionary<serverName, IList<AITool>>]
       |
       v
Agent sees all MCP tools alongside built-in tools
```

## Project Structure

```
Infrastructure.AI.MCP/
├── Services/
│   ├── McpConnectionManager.cs      Connection lifecycle (create, cache, dispose)
│   └── McpToolProvider.cs           Tool discovery and resolution
├── Resources/
│   └── TraceResourceProvider.cs     trace:// URI resource exposure
├── DependencyInjection.cs           Registers manager, provider, resources
└── Infrastructure.AI.MCP.csproj
```

## Key Types Reference

| Type | Purpose | Implements | Lifetime |
|------|---------|-----------|----------|
| `McpConnectionManager` | Connection lifecycle management | `IAsyncDisposable` | Singleton |
| `McpToolProvider` | Tool discovery across MCP servers | `IMcpToolProvider` | Singleton |
| `TraceResourceProvider` | trace:// resource exposure | `IMcpResourceProvider` | Singleton |

## Configuration

MCP servers are defined in `AppConfig.AI.McpServers.Servers`:

```jsonc
{
  "AppConfig": {
    "AI": {
      "McpServers": {
        "Servers": {
          "filesystem": {
            "Enabled": true,
            "Type": "Stdio",                     // Stdio | Http | Sse
            "Command": "npx",                    // Executable to spawn
            "Args": ["@modelcontextprotocol/server-filesystem", "/allowed/path"],
            "WorkingDirectory": null,            // Optional CWD for the process
            "Env": {},                           // Environment variables for the process
            "StartupTimeoutSeconds": 30          // How long to wait for handshake
          },
          "remote-tools": {
            "Enabled": true,
            "Type": "Http",
            "Url": "https://mcp.example.com/mcp",
            "Auth": {
              "Type": "Bearer",                  // None | ApiKey | Bearer
              "BearerToken": "eyJ...",           // For Bearer auth
              "ApiKey": null,                    // For ApiKey auth
              "ApiKeyHeader": "X-API-Key"        // Header name for ApiKey
            },
            "StartupTimeoutSeconds": 15
          }
        }
      }
    }
  }
}
```

### Auth Options

| Type | Header Used | Config Field |
|------|------------|-------------|
| `None` | (no auth) | -- |
| `Bearer` | `Authorization: Bearer {token}` | `Auth.BearerToken` |
| `ApiKey` | `{ApiKeyHeader}: {value}` | `Auth.ApiKey` + `Auth.ApiKeyHeader` |

## Common Tasks

### How to Add a New MCP Server Connection

Add an entry to `AppConfig.AI.McpServers.Servers` in appsettings.json:

```jsonc
"my-new-server": {
  "Enabled": true,
  "Type": "Stdio",
  "Command": "npx",
  "Args": ["@my-org/mcp-server", "--config", "./server-config.json"],
  "StartupTimeoutSeconds": 30
}
```

No code changes required. The connection manager will discover and connect on first tool request.

### How to Debug MCP Connection Failures

1. Check structured logs for `"Connecting to MCP server '{ServerName}' via {Transport}..."` -- confirms the attempt.
2. If the error is `McpConnectionException`, it includes the server name and transport type.
3. For Stdio servers: verify the command exists in PATH, check the Args, and ensure the process starts within `StartupTimeoutSeconds`.
4. For HTTP servers: verify the URL is reachable, check auth configuration, and look for SSL/TLS errors.
5. Check `McpServerMetrics.Requests` with `status: "error"` tag for connection failure patterns.

### How to Test Without Real MCP Servers

The `McpToolProvider` gracefully returns empty tool lists when servers are unavailable. For unit tests, mock `McpConnectionManager` to return a pre-configured `McpClient`. For integration tests without real servers, set all servers to `"Enabled": false`.

## Dependencies

**Project References:**
- `Application.AI.Common` -- `IMcpToolProvider`, `IMcpResourceProvider`, `McpConnectionException`, `McpServerMetrics`, `McpConventions`

**NuGet Packages:**
- `ModelContextProtocol` -- The official .NET MCP client SDK (transport, protocol, client API)
- `Microsoft.Extensions.Hosting` -- Host lifetime integration for connection disposal
- `Microsoft.Extensions.Options` -- `IOptionsMonitor<AIConfig>` for server configuration
- `Microsoft.Extensions.Logging` -- Structured logging

## Testing

- **Test project:** `Infrastructure.AI.MCP.Tests`
- **Run:** `dotnet test --filter "FullyQualifiedName~Infrastructure.AI.MCP.Tests"`
- **Mock guidance:** Mock `McpConnectionManager` for unit tests of `McpToolProvider`. For integration tests, use a simple Stdio MCP server (the `@modelcontextprotocol/server-everything` package provides a test server). The `TraceResourceProvider` can be tested with a temp directory and crafted `McpRequestContext` objects.
