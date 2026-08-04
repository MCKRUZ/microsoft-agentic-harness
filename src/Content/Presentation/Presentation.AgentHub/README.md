# Presentation.AgentHub

The ASP.NET Core WebAPI that serves as the backend for the Agentic Harness web experience. It is a composition root -- it wires together all application and infrastructure layers, then exposes agent capabilities through SignalR hubs (real-time streaming), AG-UI protocol SSE endpoints (standards-compliant streaming), and REST controllers (data queries). When you run the harness with a browser UI, this is what the frontend talks to.

When you launch it, you see: a Kestrel server on port 52000/52001, automatic SPA dev server launch (Vite on :5173/:5174), and Swagger UI at `/swagger`. Connected clients receive streamed agent responses token-by-token over SignalR, plus live OpenTelemetry span data for trace visualization.

## Architecture Context

```
┌─────────────────────────────────────────────────────────────────────────┐
│  Presentation.AgentHub  (Composition Root)                              │
│                                                                         │
│  Program.cs → builder.Services.GetServices() + AddAgentHubServices()    │
│                                                                         │
│  ┌──────────────────┐  ┌──────────────────┐  ┌──────────────────────┐  │
│  │  SignalR Hub      │  │  AG-UI SSE       │  │  REST Controllers    │  │
│  │  /hubs/agent      │  │  POST /ag-ui/run │  │  /api/agents         │  │
│  │                   │  │                   │  │  /api/conversations  │  │
│  │  TokenReceived    │  │  TEXT_MESSAGE_*   │  │  /api/mcp/*          │  │
│  │  TurnComplete     │  │  RUN_ERROR        │  │  /api/documents/*    │  │
│  │  SpanReceived     │  │  RUN_FINISHED     │  │  /api/metrics/*      │  │
│  └────────┬─────────┘  └────────┬─────────┘  │  /api/sessions/*     │  │
│           │                      │             └──────────┬───────────┘  │
│           └──────────────────────┴────────────────────────┘              │
│                                    │                                     │
│                         MediatR (CQRS dispatch)                          │
│                                    │                                     │
│                    ┌───────────────┴───────────────┐                     │
│                    │  ExecuteAgentTurnCommand       │                     │
│                    │  RunConversationCommand        │                     │
│                    │  IngestDocumentCommand         │                     │
│                    │  SearchDocumentsQuery          │                     │
│                    └───────────────────────────────┘                     │
└─────────────────────────────────────────────────────────────────────────┘
        │
        ▼
Presentation.Common → Infrastructure.* → Application.* → Domain.*
```

## Key Concepts

### SignalR Hub (Real-Time Agent Interaction)

`AgentTelemetryHub` at `/hubs/agent` is the primary real-time channel. Clients connect via WebSocket (with fallback to Long Polling), authenticate via Azure AD JWT (passed as `access_token` query param for WebSocket upgrades), and invoke hub methods for conversation lifecycle.

**Client-to-Server Methods:**

| Method | Purpose |
|--------|---------|
| `StartConversation(agentName, conversationId)` | Create/resume a conversation, returns history |
| `SendMessage(conversationId, messageId, content)` | Send user message, triggers agent turn |
| `RetryFromMessage(conversationId, assistantMessageId)` | Regenerate from a specific point |
| `EditAndResubmit(conversationId, userMessageId, newContent)` | Edit and re-run |
| `SetConversationSettings(conversationId, settings)` | Adjust temperature/model/system prompt |
| `InvokeToolViaAgent(conversationId, toolName, argsJson)` | Direct tool invocation through agent |
| `JoinConversationGroup` / `LeaveConversationGroup` | Subscribe to conversation-scoped spans |
| `JoinGlobalTraces` / `LeaveGlobalTraces` | Subscribe to all OTel spans |

**Server-to-Client Events:**

| Event | Payload | When |
|-------|---------|------|
| `TokenReceived` | `{ conversationId, token, isComplete }` | Each streamed token chunk |
| `TurnComplete` | `{ conversationId, turnNumber, fullResponse, assistantMessageId }` | Agent turn done |
| `ToolCallStarted` | `{ conversationId, toolName, input }` | Tool execution begins |
| `ToolCallCompleted` | `{ conversationId, toolName, output, durationMs }` | Tool execution ends |
| `SpanReceived` | Serialized span data | OTel span routed to client |
| `Error` | `{ message }` | Turn failure (sanitized, no internals) |
| `HistoryTruncated` | `{ conversationId, keepCount }` | Context window compaction occurred |

**Concurrency safety:** `ConversationLockRegistry` (singleton) provides one `SemaphoreSlim` per conversation. Concurrent `SendMessage` calls are serialized to prevent token stream interleaving or conversation record corruption. This registry is **in-process**, so it serializes turns within this host only — see issue #235 for the cross-host lease that supersedes it.

**Transcript persistence:** this host does not own the conversation store. `IConversationStore` lives in `Application.AI.Common/Interfaces/AI/`, its DTOs in `Application.AI.Common/Models/Conversations/`, and both implementations in `Infrastructure.AI/Conversations/`, registered by `AddInfrastructureAIDependencies`. It moved out of this project because the Execution API needs the same transcripts and peer Presentation projects cannot reference each other.

Two providers, selected by `AppConfig:AI:Conversations:Provider`:

| Provider | Implementation | Fit |
|----------|----------------|-----|
| `Sqlite` (default) | `EfCoreConversationStore` over `ConversationDbContext` | Any number of hosts on one machine. One row per message, so appending is an `INSERT`; SQLite's file locking serializes writers across processes and WAL mode keeps readers unblocked. Database at `AppConfig:AI:Conversations:DatabasePath`. |
| `FileSystem` | `FileSystemConversationStore` | Single-process development only. Its write lock is one in-process `SemaphoreSlim`, and every write stages through the same `.tmp` path, so two hosts sharing a directory can move a torn record into place. Directory at `AppConfig:AI:Conversations:ConversationsPath`. |

Neither guarantee crosses a machine boundary. A horizontally scaled deployment needs a server-backed implementation behind the same interface.

**Upgrading from a file-backed deployment.** The default changed to `Sqlite`, and there is no migration between the two — a host that already has JSON transcripts and takes this change will start from an empty conversation list, with the old files untouched on disk. Set `AppConfig:AI:Conversations:Provider` to `FileSystem` to keep reading them. No migrator ships because the two schemas are not equivalent (the file store has no message ordinals) and a lossy automatic conversion of an audit record is worse than an explicit choice.

**Sharing transcripts between hosts.** `DatabasePath` is relative to each host's own output directory by default, so the AgentHub and the Execution API get *separate* databases unless both are pointed at the same absolute path. Two hosts that mean to continue each other's conversations must configure that explicitly.

### AG-UI Protocol (SSE Streaming)

`POST /ag-ui/run` implements the AG-UI standard for agent streaming via Server-Sent Events. The `AgUiRunHandler` orchestrates the run lifecycle: receives the run request, dispatches to the agent pipeline, and writes `TEXT_MESSAGE_START`, `TEXT_MESSAGE_CONTENT`, `TEXT_MESSAGE_END`, `RUN_ERROR`, and `RUN_FINISHED` events to the SSE stream.

### Authentication

Two modes, controlled by `Auth:Disabled` in configuration:

**Production:** Azure AD JWT via `Microsoft.Identity.Web`. Token validation with zero clock skew, issuer/audience/signing key enforcement. SignalR WebSocket upgrades extract the token from the `access_token` query parameter.

**Development:** When `Auth:Disabled=true` AND environment is Development, `DevAuthHandler` auto-authenticates every request as a synthetic dev user. Double-guarded to prevent accidental production exposure.

### SPA Proxy (Vite Dev Servers)

Build configurations control which frontend SPAs are launched:

| Configuration | Launched SPAs | Ports |
|---------------|---------------|-------|
| `Debug` (default) | WebUI + Dashboard | :5173 + :5174 |
| `Debug-AgentHub-WebUI` | WebUI only | :5173 |
| `Debug-AgentHub-Dashboard` | Dashboard only | :5174 |
| `Debug-AgentHub-All` | Both explicitly | :5173 + :5174 |
| `Release` | None (pre-built static files) | -- |

### OpenTelemetry Span Bridge

`SignalRSpanExporter` is both an `IHostedService` (drain loop) and a `BaseExporter<Activity>`. It receives OTel spans from the pipeline and forwards them to SignalR clients subscribed to conversation-scoped or global trace groups. This enables real-time in-browser trace visualization.

## Project Structure

```
Presentation.AgentHub/
├── AgUi/
│   ├── AgUiEndpoints.cs              POST /ag-ui/run endpoint registration
│   ├── AgUiRunHandler.cs             SSE run orchestration
│   ├── AgUiEventWriter.cs            SSE event serialization
│   ├── AgUiEvents.cs                 AG-UI event type definitions
│   ├── AgUiEventType.cs              Event type enum
│   └── AgUiModels.cs                 Request/response models
├── Auth/
│   └── DevAuthHandler.cs             Development auth bypass
├── Config/
│   ├── AgentHubConfig.cs             Hub-specific settings
│   ├── AgentHubCorsConfig.cs         CORS origin list
│   └── PrometheusConfig.cs           Prometheus query proxy settings
├── Controllers/
│   ├── AgentsController.cs           Agent listing + conversation CRUD
│   ├── ClientLogsController.cs       Frontend log ingestion
│   ├── ConfigController.cs           Auth mode + deployment queries
│   ├── DocumentsController.cs        RAG document ingest + search
│   ├── McpController.cs              MCP tools/resources/prompts
│   ├── MetricsController.cs          Prometheus query proxy + catalog
│   ├── SessionsController.cs         Observability session viewer
│   └── TestConversationsController.cs Development test endpoints
├── DTOs/                             Request/response shapes
├── Hubs/
│   └── AgentTelemetryHub.cs          SignalR hub (conversation + telemetry)
├── Services/
│   ├── ConversationLockRegistry.cs   Per-conversation SemaphoreSlim
│   ├── PrometheusQueryService.cs     PromQL HTTP proxy
│   ├── DemoMetricsService.cs         Synthetic metrics for development
│   ├── SessionIdleCleanupService.cs  Auto-complete idle sessions
│   └── SignalRSpanExporter.cs        OTel → SignalR span bridge
├── Telemetry/
│   └── SignalRSpanExporter.cs        (or here depending on organization)
├── DependencyInjection.cs            AddAgentHubServices()
├── Program.cs                        Composition root entry point
├── appsettings.json                  Default configuration
└── appsettings.Development.json      Development overrides
```

## Configuration

```json
{
  "Auth": { "Disabled": true },
  "AzureAd": {
    "TenantId": "-- user-secrets --",
    "ClientId": "-- user-secrets --",
    "Audience": "api://{apiClientId}"
  },
  "AppConfig": {
    "AI": {
      "AgentFramework": { "DefaultDeployment": "gpt-4o", "ClientType": "AzureOpenAI" },
      "Conversations": { "Provider": "Sqlite", "DatabasePath": "data/conversations.db" },
      "Governance": { "Enabled": true, "PolicyPaths": ["Policies/default-policy.yaml"] }
    },
    "AgentHub": {
      "DefaultAgentName": "",
      "MaxHistoryMessages": 20,
      "Cors": { "AllowedOrigins": ["http://localhost:5173", "http://localhost:5174"] }
    },
    "Prometheus": { "BaseUrl": "http://localhost:9090", "TimeoutSeconds": 30, "EnableDemoData": false },
    "Observability": {
      "PostgresConnectionString": "Host=localhost;Port=5432;Database=observability;...",
      "Exporters": { "Otlp": { "Enabled": true, "Endpoint": "http://localhost:4317" } }
    }
  },
  "Kestrel": {
    "Endpoints": {
      "Http": { "Url": "http://*:52000" },
      "Https": { "Url": "https://*:52001" }
    }
  }
}
```

User secrets shared with ConsoleUI: `UserSecretsId: agentic-harness-console-ui`

## How to Run

```bash
# Prerequisites: .NET 10 SDK, Node.js (for SPA dev servers)

# Configure secrets (first time only)
dotnet user-secrets set "AppConfig:AI:AgentFramework:Endpoint" "https://your-endpoint.openai.azure.com/" --id agentic-harness-console-ui
dotnet user-secrets set "AppConfig:AI:AgentFramework:ApiKey" "your-key" --id agentic-harness-console-ui

# Run (launches Kestrel + Vite dev server)
dotnet run --project src/Content/Presentation/Presentation.AgentHub

# API available at: http://localhost:52000
# Swagger UI: http://localhost:52000/swagger
# WebUI: http://localhost:5173 (proxied by Vite)
# Health: http://localhost:52000/health
# Metrics: http://localhost:52000/metrics
```

### Optional Infrastructure (for full observability)

```bash
.\scripts\start-infrastructure.ps1
# Starts: PostgreSQL (:5432), Jaeger (:16686), Prometheus (:9090), OTel Collector (:4317)
```

## Common Tasks

### Adding a New REST Endpoint

1. Create/extend a controller in `Controllers/`
2. Add `[Authorize]` (or `[AllowAnonymous]` with justification)
3. Dispatch work via MediatR: `_mediator.Send(new MyCommand(...))`
4. Return appropriate `IResult` (`Results.Ok`, `Results.NotFound`, etc.)

### Adding a New Hub Method

1. Add the method to `AgentTelemetryHub` with `[Authorize]`
2. Call `ValidateOwnershipAsync()` for conversation-scoped methods
3. Acquire the conversation lock via `ConversationLockRegistry`
4. Emit server events via `Clients.Caller.SendAsync("EventName", payload)`

### Adding a New AG-UI Event Type

1. Add the event class in `AgUi/AgUiEvents.cs`
2. Add the type to `AgUiEventType` enum
3. Emit it in `AgUiRunHandler` at the appropriate lifecycle point
4. Serialize via `AgUiEventWriter.WriteEvent()`

## Dependencies

**Project References:**
- `Presentation.Common` -- composition root (`GetServices()`), OpenTelemetry, health checks
- `Presentation.WebUI` (conditional) -- React SPA build orchestration
- `Presentation.Dashboard` (conditional) -- Dashboard SPA build orchestration

**NuGet Packages:**
- `OpenTelemetry` / `OpenTelemetry.Extensions.Hosting` -- custom span exporter
- `Microsoft.Identity.Web` -- Azure AD JWT authentication
- `Microsoft.AspNetCore.SpaProxy` -- Vite dev server proxy (Debug only)

**Transitive (via Presentation.Common):**
- MediatR, FluentValidation, Microsoft.Extensions.AI, SignalR, all Infrastructure layers

## Testing

**Test project:** `Tests/Presentation.AgentHub.Tests`

```bash
dotnet test src/AgenticHarness.slnx --filter "FullyQualifiedName~Presentation.AgentHub"
```

**Coverage areas:**
- Hub method authorization and ownership enforcement
- SignalR connection lifecycle (connect, reconnect, disconnect)
- AG-UI SSE event serialization
- Rate limiter enforcement (MCP tool invoke)
- Dev auth bypass (only active in Development + Auth:Disabled)
- Span exporter routing (conversation-scoped vs global)
