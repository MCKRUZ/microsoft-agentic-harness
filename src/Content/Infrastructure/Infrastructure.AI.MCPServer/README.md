# Infrastructure.AI.MCPServer

The MCP client (Infrastructure.AI.MCP) reaches out to consume external tools. This project goes the other direction — it *exposes* the harness's own tools, prompts, and resources to external systems via the Model Context Protocol.

This is a standalone ASP.NET Core WebAPI application that turns the harness into an MCP server. External agents, IDEs, automation pipelines, and other MCP clients can discover and invoke the harness's capabilities through a standardized protocol.

---

## What It Exposes

The MCP server doesn't hardcode which tools, prompts, or resources are available. Instead, it uses **reflection-based assembly scanning**: the `McpConfig.ScanAssemblies` configuration lists which assemblies to scan, and `McpServerBuilderExtensions` dynamically discovers and registers everything marked with MCP attributes.

This means adding a new tool to the MCP surface is a matter of implementing it in the right assembly with the right attributes. No registration code to touch, no endpoint to add.

## Authentication

Two modes, selected by configuration:

- **Azure Entra ID (JWT Bearer)** — Full token validation with issuer, audience, and signing key checks. Standard for production deployments.
- **Anonymous** — No authentication. For local development and testing only.

The authentication mode is configured through `McpConfig.Auth`, not hardcoded. The server reads the setting at startup and conditionally wires JWT validation.

## Rate Limiting

A fixed-window rate limiter protects the MCP endpoints: 100 requests per minute by default. This prevents a misconfigured or malicious client from overwhelming the server. The rate limit is applied globally to all MCP endpoints.

## Resource Subscriptions

MCP supports resource subscriptions — clients can register for change notifications on specific resources. The server tracks active subscriptions in a `ConcurrentDictionary` and provides handlers for subscribe/unsubscribe lifecycle events.

---

## Project Structure

```
Infrastructure.AI.MCPServer/
├── Extensions/
│   ├── McpServerExtensions.cs          JWT auth, transport config, event handlers
│   └── McpServerBuilderExtensions.cs   Reflection-based tool/prompt/resource discovery
└── Program.cs                          ASP.NET Core entry point (Kestrel + MCP + auth + rate limiting)
```

## Dependencies

- **Application.AI.Common** / **Application.Common** — Shared interfaces and DI patterns
- **Domain.Common** — `AppConfig.AI.MCP` configuration
- **ModelContextProtocol.AspNetCore** — MCP server SDK with ASP.NET Core integration
- **Microsoft.AspNetCore.Authentication.JwtBearer** — JWT token validation
