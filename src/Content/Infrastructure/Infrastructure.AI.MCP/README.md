# Infrastructure.AI.MCP

The MCP client is how the harness reaches out to the broader tool ecosystem. When an MCP server somewhere exposes a tool — a code search engine, a database query interface, a deployment pipeline — this project discovers it, connects to it, and makes it available to agents as if it were a built-in tool.

Three files. One job: extend the agent's capabilities beyond what's compiled into the harness.

---

## How It Works

### Connection Management

`McpConnectionManager` handles the lifecycle of MCP client connections. When the harness starts, it reads the `McpServers` configuration — a list of external MCP servers with their transport type, endpoint, and optional authentication.

Connections are lazily initialized and cached. The first time a tool from a particular server is needed, the manager creates the connection. Subsequent requests reuse it. Three transports are supported:

- **Stdio** — For local MCP servers running as child processes
- **HTTP** — Standard HTTP transport with optional Bearer token auth
- **SSE** — Server-Sent Events for streaming connections

Authentication headers are injected per-server configuration. Failed connections throw `McpConnectionException` (defined in Application.AI.Common) with transport-specific diagnostics.

The manager implements `IAsyncDisposable` for clean shutdown — all connections are disposed when the harness stops.

### Tool Discovery

`McpToolProvider` implements `IMcpToolProvider`. It iterates over configured MCP servers, connects via the manager, and discovers available tools. Each tool is returned as a Microsoft.Extensions.AI `AITool` instance — the same type the agent framework expects.

The critical design decision: **graceful degradation**. If a server is down or unreachable, the provider logs the failure and skips it. The agent loses those tools but keeps running with whatever else is available. One broken server doesn't take down the whole tool surface.

---

## Project Structure

```
Infrastructure.AI.MCP/
├── Services/
│   ├── McpConnectionManager.cs    Lazy connection pool (Stdio, HTTP, SSE)
│   └── McpToolProvider.cs         Tool discovery with graceful degradation
└── DependencyInjection.cs         Singleton registration for both services
```

## Dependencies

- **Application.AI.Common** — `IMcpToolProvider`, `McpConnectionException`
- **ModelContextProtocol** — MCP client SDK
- **Microsoft.Extensions.AI** — `AITool` contract
