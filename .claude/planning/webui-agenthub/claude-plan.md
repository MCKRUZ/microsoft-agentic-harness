# Implementation Plan: Presentation.AgentHub + Presentation.WebUI

## Overview

This plan covers the addition of two new projects to the `microsoft-agentic-harness` solution: a .NET 10 ASP.NET Core Web API (`Presentation.AgentHub`) that exposes the existing AI agent pipeline over HTTP and SignalR, and a Vite + React 19 + TypeScript browser client (`Presentation.WebUI`) that provides a chat interface and real-time MCP/telemetry inspector.

The solution already has a working AI agent pipeline accessible via `Presentation.ConsoleUI`. These two projects add a browser-based entry point to the same pipeline, demonstrating how a web front-end can be cleanly layered on top of the Clean Architecture stack. Both projects are designed as template-quality code: enterprise authentication (Azure AD), extensible panel architecture, full XML documentation on all public .NET types, and 80% test coverage.

### Why These Two Projects

The existing `ConsoleUI` demonstrates the pipeline but is not accessible to end-users outside the development machine. `AgentHub` turns the pipeline into an HTTP service so any browser can send messages, receive streamed responses, and observe the agent's internal tool execution in real time. `WebUI` is the polished consumer of that service — a reference implementation of a modern React frontend following the bulletproof-react feature-based structure, shadcn/ui component system, and MSAL authentication.

### Relationship to Existing Code

`Presentation.Common` provides a single `GetServices(includeHealthChecksUI: bool)` extension method that wires the complete DI graph in the correct order (Application → Infrastructure → Observability). `AgentHub` calls this with `includeHealthChecksUI: true` then adds only its own concerns on top: Azure AD auth, SignalR, CORS, its custom `AgentHubConfig`, and the `SignalRSpanExporter`. Nothing in the existing infrastructure needs to change.

---

## Section 1: Solution Structure and Project Scaffolding

### What to Build

Create both project directories and files, register them in `AgenticHarness.slnx`, and establish the build chain so `dotnet build src/AgenticHarness.slnx` and `dotnet test src/AgenticHarness.slnx` succeed before any application logic is written.

### AgentHub Project Files

Create `src/Content/Presentation/Presentation.AgentHub/` as a standard ASP.NET Core Web API project targeting `net10.0`. The `.csproj` must enable `<Nullable>enable</Nullable>` and `<ImplicitUsings>enable</ImplicitUsings>`. It needs package references to:
- `Microsoft.Identity.Web` for Azure AD authentication
- `Microsoft.AspNetCore.SignalR` (included in the framework, no separate NuGet needed for .NET 10)
- `Microsoft.Extensions.Hosting` for `IHostedService`
- `OpenTelemetry` SDK (version matching the rest of the solution)
- A project reference to `Presentation.Common`

Additionally create `src/Content/Tests/Presentation.AgentHub.Tests/` with an xUnit test project referencing `Presentation.AgentHub`, `Microsoft.AspNetCore.Mvc.Testing`, and a project reference to `Presentation.Common`.

### WebUI Project Files

Create `src/Content/Presentation/Presentation.WebUI/` as a Vite + React 19 + TypeScript project. The directory is not a `.csproj` — it is a Node project. Initialize with:

```
npm create vite@latest . -- --template react-ts
```

Then install dependencies in a single `npm install` pass:
- UI: `tailwindcss`, `@tailwindcss/vite`, `shadcn` (CLI, not a runtime dep)
- Auth: `@azure/msal-browser`, `@azure/msal-react`
- Real-time: `@microsoft/signalr`
- State: `zustand`, `@tanstack/react-query`
- Forms: `react-hook-form`, `zod`, `@hookform/resolvers`
- Lists: `react-window`, `@types/react-window`
- HTTP: `axios`
- Dev orchestration: `concurrently`
- Testing: `vitest`, `@vitest/coverage-v8`, `@testing-library/react`, `@testing-library/user-event`, `@testing-library/jest-dom`, `msw`, `jsdom`

The `tsconfig.json` must set `"strict": true`, `"noUncheckedIndexedAccess": true`, `"noImplicitReturns": true`, and path alias `"@/*": ["src/*"]`.

### Solution Registration

Add both projects to `AgenticHarness.slnx`. The test project `Presentation.AgentHub.Tests` also goes into the solution. The WebUI project is not a .NET project and should not be added to the `.slnx` — it is managed separately by npm. The README should document that `cd src/Content/Presentation/Presentation.WebUI && npm install` is part of the dev setup.

---

## Section 2: Presentation.AgentHub — Core Setup

### What to Build

`Program.cs`, `DependencyInjection.cs`, `appsettings.json`, and the Azure AD + CORS + SignalR wiring. After this section the project should build, start, and return 401 on all endpoints (auth is wired, no routes yet).

### Program.cs Structure

`Program.cs` is a minimal top-level file. Its responsibilities in order:
1. Call `services.GetServices(includeHealthChecksUI: true)` from `Presentation.Common` — this registers the entire agent stack.
2. Call `services.AddAgentHubServices(builder.Configuration)` — the local `DependencyInjection.cs` extension that adds auth, SignalR, CORS, and the OTel exporter.
3. Configure Kestrel to listen on port 5001.
4. Build the app.
5. In the middleware pipeline, order is critical: `UseRouting()` → `UseCors("AgentHubCors")` → `UseAuthentication()` → `UseAuthorization()` → `MapControllers()` → `MapHub<AgentTelemetryHub>("/hubs/agent")` → health check endpoints. `UseCors` must come after `UseRouting` and before `UseAuthentication` to ensure CORS preflight requests are handled before auth middleware rejects them.

### DependencyInjection.cs

The `AddAgentHubServices(IConfiguration)` extension method registers:

**Azure AD auth:**
Bind the `AzureAd` configuration section (TenantId, ClientId, Audience) and call `services.AddMicrosoftIdentityWebApiAuthentication(configuration)`. Then explicitly configure `JwtBearerOptions.Events.OnMessageReceived` to extract the `?access_token=` query parameter when the path starts with `/hubs` — `AddMicrosoftIdentityWebApiAuthentication` does not do this automatically for SignalR WebSocket upgrade paths. Without this, WebSocket connections will fail authentication.

**SignalR:**
`services.AddSignalR(opts => opts.CloseOnAuthenticationExpiration = true)`. No custom serialization needed for the POC; default System.Text.Json is fine.

**Rate limiting:**
`services.AddRateLimiter(...)` with a fixed-window policy on `/api/mcp/tools/{name}/invoke` (10 requests/minute per IP) and a token-bucket policy on hub message invocations (10 `SendMessage` calls/minute per connection). Register `app.UseRateLimiter()` in the middleware pipeline between `UseAuthorization()` and `MapControllers()`.

**CORS:**
Policy named `"AgentHubCors"`. Allowed origins from `AppConfig:AgentHub:Cors:AllowedOrigins`. Allow any method and header. Do NOT call `AllowCredentials()` — this API uses Bearer token authentication, not cookies, so credentials mode is not needed and enabling it unnecessarily restricts allowed origins and increases CORS risk. In development, always include `http://localhost:5173`.

**AgentHubConfig:**
Bind `AppConfig:AgentHub` to a strongly-typed `AgentHubConfig` record with `ConversationsPath` (string), `DefaultAgentName` (string), and `Cors` section. Register via `services.Configure<AgentHubConfig>(...)`.

**SignalRSpanExporter:**
Register as both `SignalRSpanExporter` singleton and `IHostedService`. This is described in detail in Section 5.

**IConversationStore:**
Register `FileSystemConversationStore` as the `IConversationStore` implementation. Detail in Section 3.

### appsettings.json Shape

The new `AppConfig:AgentHub` section sits alongside existing sections:
```
AppConfig:AgentHub:ConversationsPath  (string, default "./conversations")
AppConfig:AgentHub:DefaultAgentName   (string)
AppConfig:AgentHub:Cors:AllowedOrigins (string array)
AzureAd:TenantId, AzureAd:ClientId, AzureAd:Audience
```
The `AzureAd` section lives at root level, not under `AppConfig`, because `Microsoft.Identity.Web` binds it from `configuration.GetSection("AzureAd")` by convention.

---

## Section 3: Presentation.AgentHub — Conversation Store and Agent Execution

### What to Build

`IConversationStore` interface, `FileSystemConversationStore` implementation, `ConversationMessage` and `ConversationRecord` domain types, and `AgentsController` for listing configured agents and managing conversation history.

### Domain Types

`ConversationMessage` is a record with: `Role` (enum: `User`, `Assistant`, `System`, `Tool`), `Content` (string), `Timestamp` (DateTimeOffset), and optional `ToolCalls` (array of `ToolCallRecord`). `ToolCallRecord` has `ToolName`, `Input` (JsonElement), `Output` (JsonElement or string), `DurationMs` (long).

`ConversationRecord` is a record with: `Id` (Guid as string), `AgentName`, `UserId`, `CreatedAt`, `UpdatedAt`, and `Messages` (IReadOnlyList of `ConversationMessage`).

### IConversationStore Interface

Methods:
- `Task<ConversationRecord?> GetAsync(string conversationId, CancellationToken ct)`
- `Task<IReadOnlyList<ConversationRecord>> ListAsync(string userId, CancellationToken ct)`
- `Task<ConversationRecord> CreateAsync(string agentName, string userId, CancellationToken ct)` — generates a new Guid ID
- `Task AppendMessageAsync(string conversationId, ConversationMessage message, CancellationToken ct)`
- `Task DeleteAsync(string conversationId, CancellationToken ct)`

### FileSystemConversationStore

Stores each `ConversationRecord` as a JSON file at `{ConversationsPath}/{conversationId}.json`. Uses `System.Text.Json` for serialization.

**Path safety:** On construction, call `Path.GetFullPath(ConversationsPath)` and store the resolved absolute path. Reject any operation where the resolved file path does not start with this base path (prevents path traversal via crafted conversationIds).

**Thread-safety:** Use a single `SemaphoreSlim(1, 1)` for the POC (all file operations serialized). This is intentionally simple — a production implementation would use `AsyncKeyedLock` for per-file granularity. Document this in an XML doc comment on the class.

**Atomic writes:** Never write JSON directly to `{conversationId}.json`. Write to `{conversationId}.tmp` first, then call `File.Move(tmpPath, finalPath, overwrite: true)`. This prevents partial-write corruption on crash.

**User listing:** `ListAsync(userId)` reads all `.json` files in the directory, deserializes each, and filters by `ConversationRecord.UserId`. Document in the XML comment that this is O(n) in the number of conversations — acceptable for a POC, not for production at scale.

**Conversation truncation:** `GetHistoryForDispatch(string conversationId, int maxMessages)` returns the last `maxMessages` entries from `Messages`. Called by the hub before dispatching `ExecuteAgentTurnCommand`. Default `maxMessages` from `AppConfig:AgentHub:MaxHistoryMessages` (default: 20). Prevents unbounded token growth.

On startup, ensures the conversations directory exists. Creates it if missing.

### AgentsController

`GET /api/agents` — reads agent names from `AppConfig:AI:AgentFramework` (or wherever agent configs live in the existing `AppConfig` hierarchy) and returns a list of `AgentSummary` DTOs: `{ Name, Description }`. Protected with `[Authorize]`.

`GET /api/conversations` — calls `IConversationStore.ListAsync` with the current user's identity from `HttpContext.User`. Returns list of conversation summaries.

`GET /api/conversations/{id}` — returns full `ConversationRecord`. Returns 404 if not found. Returns 403 if `ConversationRecord.UserId` does not match the current user's identity claim (`User.GetUserId()`).

`DELETE /api/conversations/{id}` — validates ownership first (403 if not owner), then deletes. Returns 204.

### Agent Execution in the Hub

The `AgentTelemetryHub` (Section 4) dispatches `ExecuteAgentTurnCommand` via `IMediator`. The command takes `AgentName`, `UserMessage`, `ConversationHistory` (truncated to `MaxHistoryMessages` via `GetHistoryForDispatch`), and `ConversationId`. Before dispatching, set an Activity tag `agent.conversation_id = conversationId` on the current `Activity` so the OTel exporter can route spans to the correct SignalR group (see Section 5). After the turn completes, append both the user message and the assistant response to `IConversationStore`. If the mediator throws, catch the exception, append a synthetic assistant `Error` message so conversation state remains coherent, and send a sanitized `Error` event to the client (log full exception server-side, never surface stack traces or internal details to the client).

---

## Section 4: Presentation.AgentHub — SignalR Hub

### What to Build

`AgentTelemetryHub` with all client-to-server methods, and the real-time event broadcasting logic for streaming chat responses and telemetry. After this section the hub accepts WebSocket connections (with a valid Azure AD token) and routes messages to the agent pipeline.

### Hub Structure

`AgentTelemetryHub` extends `Hub` and is decorated with `[Authorize]`. It is injected with `IMediator`, `IConversationStore`, `ILogger<AgentTelemetryHub>`, and `AgentHubConfig` via constructor injection.

**Group naming convention:**
- Conversation telemetry group: `"conversation:{conversationId}"` — the client that owns this conversation joins this group on `StartConversation`.
- Global traces group: `"global-traces"` — any client can join for the firehose view.

**Client-to-server methods:**

`StartConversation(string agentName, string conversationId)` — validates ownership if `conversationId` refers to an existing record (load from store, check `UserId`; throw `HubException` on mismatch). Adds the caller to `"conversation:{conversationId}"` group via `Groups.AddToGroupAsync`. Creates the conversation record in `IConversationStore` if it doesn't exist. Returns the last 20 messages (paged) so the client can restore state on reconnect without downloading the full history.

`SendMessage(string conversationId, string userMessage)` — core method. Validates ownership first. Uses a per-conversation `SemaphoreSlim` (stored in a `ConcurrentDictionary<string, SemaphoreSlim>` on the hub, one entry per active conversation) to serialize concurrent turns — a second `SendMessage` on the same conversation waits until the first completes. On entry: appends user message to store. Dispatches `ExecuteAgentTurnCommand`. Because the existing pipeline may not support true token streaming, implement simulated streaming by chunking the response into fixed-size character segments (50 chars each — not word boundaries, to avoid problems with code, JSON, or non-space languages) sent as `TokenReceived` events with `isComplete: false`. Send a final `TokenReceived` with `isComplete: true` and the full text, then `TurnComplete`. Document with a TODO that this should be replaced with real `IAsyncEnumerable<string>` streaming when the pipeline supports it. On exception: see Section 3 "Agent Execution" for error handling.

`InvokeToolViaAgent(string conversationId, string toolName, string inputJson)` — validates ownership, then crafts a user message asking the agent to use the tool and delegates to `SendMessage`.

`JoinConversationGroup(string conversationId)` — validates ownership before `Groups.AddToGroupAsync`. Returns 403-equivalent `HubException` if the conversation does not belong to the current user.

`LeaveConversationGroup(string conversationId)` — removes caller from the group. No ownership check needed (leaving is always safe).

`JoinGlobalTraces()` — requires the caller to have the `AgentHub.Traces.ReadAll` role claim (checked via `Context.User.IsInRole("AgentHub.Traces.ReadAll")` or a configurable policy). Throws `HubException` if the claim is absent. Document that this role must be assigned in the Azure AD app registration and is disabled by default in production config. `LeaveGlobalTraces()` — removes caller; no role check needed.

**Server-to-client event names and payload shapes:**

| Event | Payload Fields |
|---|---|
| `TokenReceived` | `conversationId`, `token` (string), `isComplete` (bool) |
| `TurnComplete` | `conversationId`, `turnNumber` (int), `fullResponse` (string) |
| `ToolCallStarted` | `conversationId`, `spanId`, `toolName`, `input` (object) |
| `ToolCallCompleted` | `conversationId`, `spanId`, `toolName`, `output` (object), `durationMs` (long) |
| `SpanReceived` | full `SpanData` record (see Section 5) |
| `Error` | `conversationId`, `message`, `code` (string) |

---

## Section 5: Presentation.AgentHub — OTel → SignalR Bridge

### What to Build

`SignalRSpanExporter` (a custom OpenTelemetry `BaseExporter<Activity>` that also implements `IHostedService`) and its registration in the OTel pipeline. After this section, every Activity/span emitted by the agent pipeline will flow to connected SignalR clients in real time.

### Why BaseExporter + IHostedService

`BaseExporter<Activity>.Export()` is called synchronously from the OTel SDK background thread. We cannot `await` in `Export()`, and calling SignalR's `SendAsync` directly could block the OTel pipeline under load. The solution: `Export()` writes span data to a `Channel<SpanData>` (non-blocking). The `IHostedService.StartAsync` drains the channel in a background loop and calls `SendAsync`. A bounded channel with `DropOldest` ensures backpressure never blocks the pipeline.

### SpanData Record

```csharp
// All fields needed by the frontend trace tree
public record SpanData(
    string Name,
    string TraceId,
    string SpanId,
    string? ParentSpanId,    // null for root spans (no parent)
    string? ConversationId,  // from agent.conversation_id activity tag; null for non-agent spans
    DateTimeOffset StartTime,
    double DurationMs,
    string Status,           // "unset" | "ok" | "error"
    string? StatusDescription,
    string Kind,             // "internal" | "client" | "server"
    string SourceName,
    IReadOnlyDictionary<string, string> Tags
);
```

`ParentSpanId` is nullable because root spans have no parent. In `MapToSpanData`, set it to `null` when `activity.ParentSpanId == default(ActivitySpanId)`. The TypeScript counterpart must also be `string | null`.

### SignalRSpanExporter Design

Constructor takes `IHubContext<AgentTelemetryHub>` and `ILogger<SignalRSpanExporter>`. The channel is created in the constructor as `Channel.CreateBounded<SpanData>(new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest })`.

`Export(in Batch<Activity> batch)` — iterates the batch, calls a private `MapToSpanData(Activity)` helper, and calls `_channel.Writer.TryWrite(span)`. Returns `ExportResult.Success` always (even if some items were dropped — log a warning if `TryWrite` returns false).

`IHostedService.StartAsync(CancellationToken ct)` — starts a background `Task` that reads from `_channel.Reader.ReadAllAsync(ct)`. For each `SpanData`, perform both sends with `await Task.WhenAll(...)` directly in the drain loop — do NOT use `Task.Run` or fire-and-forget per span, which would cause GC pressure, event reordering, and swallowed exceptions:
1. If `span.ConversationId` is not null: send to `"conversation:{span.ConversationId}"` group
2. Send to `"global-traces"` group (firehose subscribers)

The `ConversationId` on `SpanData` comes from the `agent.conversation_id` Activity tag set by the hub before dispatching the command (see Section 4). This is the correct correlation key — `TraceId` is a different identifier and must not be used as the group key.

`IHostedService.StopAsync(CancellationToken ct)` — completes the channel writer so `ReadAllAsync` terminates.

### Registration

In `DependencyInjection.cs`, after registering SignalR:
```
services.AddSingleton<SignalRSpanExporter>();
services.AddHostedService(sp => sp.GetRequiredService<SignalRSpanExporter>());
```

In the OTel pipeline (inside the `ITelemetryConfigurator` that `Infrastructure.Observability` uses, or by extending it from AgentHub's DI), add:
```
.AddExporter(sp => sp.GetRequiredService<SignalRSpanExporter>())
```

Because `Infrastructure.Observability` must be registered last (its `ITelemetryConfigurator` order is 300), and the `SignalRSpanExporter` is a simple exporter added after the OTel pipeline is built, it should be added via `.WithTracing(b => b.AddExporter(...))` in `AddAgentHubServices` after `GetServices()` has run.

---

## Section 6: Presentation.AgentHub — MCP Artifacts API

### What to Build

`McpController` with endpoints for listing and invoking MCP tools, resources, and prompts. These endpoints are consumed by the WebUI's Tools/Resources/Prompts tabs.

### Controller Structure

`McpController` is an `[ApiController]` `[Route("api/mcp")]` `[Authorize]` controller. It is injected with `IMcpToolProvider` and `IMcpResourceProvider` (both already in DI from the `GetServices()` call).

**Endpoints:**

`GET /api/mcp/tools` — calls `IMcpToolProvider` to enumerate available tools. Returns an array of `McpToolDto`: `{ Name, Description, Schema (JsonElement) }`. The schema is the JSON Schema object the tool accepts.

`GET /api/mcp/resources` — calls `IMcpResourceProvider` to enumerate resources. Returns `McpResourceDto[]`: `{ Uri, Name, Description, MimeType }`.

`GET /api/mcp/prompts` — if `IMcpPromptProvider` exists in the container, enumerate prompts. Returns `McpPromptDto[]`: `{ Name, Description, Arguments[] }`. If no prompt provider is registered, returns an empty array (do not throw).

`POST /api/mcp/tools/{name}/invoke` — accepts `McpToolInvokeRequest { Arguments: JsonElement }`. Enforces: request body size limit (32KB max via `[RequestSizeLimit]`). Emits a structured audit log entry at `Information` level: `{ UserId, ToolName, InputHash (SHA-256 of serialized arguments), Timestamp }` — never log raw arguments at `Information` level as they may contain sensitive data; raw arguments logged only at `Debug`. Calls `IMcpToolProvider` to invoke the named tool. Returns `McpToolInvokeResponse { Output: JsonElement, DurationMs: long, Success: bool, Error: string? }`.

### Error Handling

Tool not found → 404. Tool execution failure → 200 with `Success: false` and a sanitized error message in the response body (never include stack traces or internal paths in the response). Log full exception server-side at `Error` level.

---

## Section 7: Presentation.AgentHub — Tests

### What to Build

Integration and unit tests covering: hub authentication, conversation store, agent execution via hub, and MCP endpoints. Target: 80% line coverage.

### Test Infrastructure

Use `WebApplicationFactory<Program>` with a custom `TestWebApplicationFactory` subclass that:
1. Replaces Azure AD auth with a `TestAuthHandler` (always authenticates as `"test-user"`)
2. Uses a temp directory for `FileSystemConversationStore`
3. Registers a mock `IMediator` via `Moq` so tests don't make real AI calls

`TestAuthHandler` is a custom `AuthenticationHandler<AuthenticationSchemeOptions>` that reads a `x-test-user` header or always returns a fixed `ClaimsPrincipal`. This is a standard pattern from ASP.NET Core testing docs.

### Hub Integration Tests

Use `WebApplicationFactory` + `HubConnection` pointed at the in-process test server. Key tests:
- Unauthenticated connection is rejected (401)
- `StartConversation` creates a new conversation record
- `SendMessage` invokes `IMediator.Send` with an `ExecuteAgentTurnCommand`
- `SendMessage` emits `TokenReceived` and `TurnComplete` events on the group
- `JoinGlobalTraces` and then send a message — `SpanReceived` should arrive
- **IDOR**: user A cannot call `StartConversation` with user B's conversationId — expects `HubException`
- **IDOR**: user A cannot call `SendMessage` on user B's conversation — expects `HubException`
- **Global traces role gate**: user without `AgentHub.Traces.ReadAll` role receives `HubException` on `JoinGlobalTraces`
- **Turn serialization**: two rapid `SendMessage` calls on the same conversation complete in order with non-interleaved events

### Conversation Store Tests

Unit tests for `FileSystemConversationStore` using a `TempDirectory` fixture:
- `CreateAsync` creates a JSON file
- `AppendMessageAsync` updates the file
- `GetAsync` deserializes correctly
- `DeleteAsync` removes the file
- Concurrent writes to the same conversation don't corrupt the file (semaphore test)

### MCP Controller Tests

Standard `WebApplicationFactory` HTTP tests:
- `GET /api/mcp/tools` returns 200 with tool list
- `POST /api/mcp/tools/{name}/invoke` with valid args returns 200 with output
- `POST /api/mcp/tools/nonexistent/invoke` returns 404

---

## Section 8: Presentation.WebUI — Project Setup and App Shell

### What to Build

The full Vite + React scaffold, Tailwind and shadcn initialization, TypeScript strict config, folder structure, MSAL provider wiring, React Router setup, and the app shell layout (header + split panel). After this section the app loads in the browser, shows the Azure AD login prompt, and after auth shows an empty two-panel layout.

### Folder Structure

```
src/
  app/
    App.tsx         # Root: MsalProvider > QueryClientProvider > ThemeProvider > Router
    main.tsx        # Vite entry, ReactDOM.createRoot
    providers.tsx   # Composes all providers
    router.tsx      # React Router routes: / = MainLayout, /auth/callback = redirect handler
  components/
    layout/
      AppShell.tsx  # Header + SplitPanel
      SplitPanel.tsx
      Header.tsx    # Agent selector (placeholder), theme toggle, user display
    ui/             # shadcn/ui copied components: Button, Input, Badge, Tabs, Separator, etc.
    theme/
      ThemeProvider.tsx
  features/         # (populated in later sections)
  hooks/
    useAgentHub.ts
    useTheme.ts
  lib/
    authConfig.ts
    apiClient.ts
    queryClient.ts
    signalrClient.ts
  stores/
    appStore.ts
  types/
    api.ts
    signalr.ts
  test/
    setup.ts
    utils.tsx
```

### MSAL Configuration

`src/lib/authConfig.ts` exports `msalConfig: Configuration` reading from `import.meta.env.VITE_AZURE_CLIENT_ID` and `VITE_AZURE_TENANT_ID`. Also exports `loginRequest: PopupRequest` with the API scope (`api://{clientId}/.default`). A `.env.example` file (committed) documents the required variables; `.env.local` (gitignored) holds the actual values.

### App.tsx

Wraps the tree: `MsalProvider(msalConfig)` → `QueryClientProvider(queryClient)` → `ThemeProvider` → `BrowserRouter` → `Routes`. The main route renders `AppShell` only when `AuthenticatedTemplate` is satisfied; otherwise renders a login button that calls `instance.loginRedirect(loginRequest)`.

### AppShell

A flexbox layout: full-viewport-height header (64px) + flex-1 content area. Content area is `SplitPanel` — resizable left (chat) and right (MCP/telemetry) panels. Initial split: 40% left / 60% right. `SplitPanel` uses CSS `grid-template-columns` with a draggable divider.

### Header

Left: app name + agent selector dropdown (populated later in Section 11). Center: empty for now. Right: theme toggle icon button + user display (username from MSAL `account.name`) + sign-out button.

### Theme

`ThemeProvider` sets `data-theme="dark"` or `data-theme="light"` on `<html>`. Tailwind's `darkMode: 'class'` (or `data-theme` variant) drives the color scheme. Initial value from `localStorage.getItem('theme')` falling back to system preference `prefers-color-scheme`.

---

## Section 9: Presentation.WebUI — MSAL Auth and API Client

### What to Build

`useAuth` hook, `apiClient` (axios with token interceptor), and `signalrClient` factory. After this section authenticated REST calls and SignalR connections work from the browser.

### useAuth Hook

Wraps `useMsal()`. Exposes:
- `account: AccountInfo | null`
- `isAuthenticated: boolean`
- `acquireToken(): Promise<string>` — calls `instance.acquireTokenSilent({ account, scopes })`, falls back to `acquireTokenPopup` on `InteractionRequiredAuthError`
- `signOut(): void`

### API Client

`src/lib/apiClient.ts` creates an `axios` instance with `baseURL: import.meta.env.VITE_API_BASE_URL`. A request interceptor calls `acquireTokenSilent` (from MSAL's `useMsal` hook — note: this must be called via a closure that has access to the MSAL instance; use a module-level `setMsalInstance` setter called from `App.tsx`) and sets `Authorization: Bearer {token}`. A response interceptor handles 401 by redirecting to login.

### SignalR Client Factory

`src/lib/signalrClient.ts` exports `buildHubConnection(path: string, getToken: () => Promise<string>): HubConnection`. Uses:
```
HubConnectionBuilder
  .withUrl(path, { accessTokenFactory: getToken })
  .withAutomaticReconnect([0, 2000, 10000, 30000])
  .configureLogging(LogLevel.Warning)
  .build()
```

### useAgentHub Hook

`src/hooks/useAgentHub.ts` is the central real-time hook. It:
1. Creates a `HubConnection` via `buildHubConnection('/hubs/agent', () => useAuth().acquireToken())` — stored in `useRef`.
2. Tracks `connectionState: 'disconnected' | 'connecting' | 'connected' | 'reconnecting'` in `useState`.
3. Registers all SignalR event handlers: routes each event to the appropriate Zustand store action.
4. Exposes: `connectionState`, `sendMessage(conversationId, message)`, `startConversation(agentName, conversationId)`, `invokeToolViaAgent(...)`, `joinGlobalTraces()`, `leaveGlobalTraces()`.
5. `useEffect` cleanup calls `connection.stop()` — handles React 19 StrictMode double-invoke correctly.

---

## Section 10: Presentation.WebUI — Chat Feature

### What to Build

The left panel: message list, streaming token rendering, typing indicator, and chat input form. After this section a user can type a message, it appears optimistically, the agent responds with streaming tokens, and the full conversation is scrollable.

### State: useChatStore

`src/features/chat/useChatStore.ts` using Zustand. State shape:
```typescript
interface ChatState {
  conversationId: string | null;
  messages: ChatMessage[];
  isStreaming: boolean;
  streamingContent: string;  // accumulated tokens for current stream
}
```

Actions: `setConversationId`, `addMessage(ChatMessage)`, `appendToken(token: string)`, `finalizeStream(fullResponse: string)`, `clearMessages`.

`ChatMessage` type: `{ id: string, role: 'user' | 'assistant', content: string, timestamp: Date, toolCalls?: ToolCallSummary[] }`.

`useAgentHub`'s event handlers dispatch to this store: `TokenReceived` → `appendToken`, `TurnComplete` → `finalizeStream` + `addMessage`.

### ChatPanel

Root component for the left panel. Contains: `ConversationHeader` (shows conversation ID, clear button), `MessageList`, `TypingIndicator` (conditional), `ChatInput`.

On mount, calls `useAgentHub().startConversation(selectedAgent, conversationId)` where `conversationId` is from `useChatStore` (or creates a new UUID).

### MessageList

Uses `react-window`'s `VariableSizeList` for virtualization — necessary because messages can be very long. Each item renders `MessageItem`. Scrolls to bottom on new messages via `listRef.current.scrollToItem(messages.length - 1)`.

`MessageItem` renders differently by role: user messages right-aligned with a distinct background; assistant messages left-aligned with a code-block renderer for markdown fences (use a simple regex split — no full markdown library needed for the POC). Tool call summaries shown as collapsed chips below the message.

### TypingIndicator

Three animated dots. Shown when `useChatStore.isStreaming === true`. Hidden otherwise.

### ChatInput

React Hook Form with `zodResolver`. Schema: `z.object({ message: z.string().min(1).max(4000) })`. On submit: call `useAgentHub().sendMessage(conversationId, message)`, call `form.reset()`. Disabled while `isStreaming`. Submit on Enter (Shift+Enter for newline). shadcn/ui `Textarea` + `Button`.

### Streaming Token Rendering

The current assistant message shows `streamingContent` while streaming. When `TurnComplete` arrives, the message is replaced with the final `fullResponse`. This avoids the content jumping during streaming — the optimistic message and the streaming message are the same message object, just with content updating.

---

## Section 11: Presentation.WebUI — Telemetry and MCP Panel

### What to Build

The right panel with five tabs: My Traces, All Traces, Tools, Resources, Prompts. After this section the traces panel shows real-time span trees from SignalR, the tools tab shows MCP tools with invocation capability, and resources/prompts tabs show their respective artifact lists.

### Right Panel Tabs

`RightPanel.tsx` uses shadcn/ui `Tabs` with values: `my-traces`, `all-traces`, `tools`, `resources`, `prompts`. The tab list is sticky at the top; each tab panel scrolls independently.

### Telemetry State: useTelemetryStore

```typescript
const MAX_GLOBAL_SPANS = 500; // cap to prevent unbounded memory growth

interface TelemetryState {
  conversationSpans: Record<string, SpanData[]>;  // keyed by conversationId
  globalSpans: SpanData[];                         // capped at MAX_GLOBAL_SPANS
  addConversationSpan: (conversationId: string, span: SpanData) => void;
  addGlobalSpan: (span: SpanData) => void;         // trims oldest when over cap
  clearConversation: (conversationId: string) => void;
  clearAll: () => void;
}
```

`addGlobalSpan` trims the array to `MAX_GLOBAL_SPANS` by dropping oldest entries when the cap is exceeded. The "Clear" button in the All Traces tab calls `clearAll()`.

`useAgentHub`'s `SpanReceived` handler adds to `conversationSpans` (keyed by the active `conversationId`) AND to `globalSpans` (for All Traces).

### SpanData TypeScript Type

```typescript
interface SpanData {
  name: string;
  traceId: string;
  spanId: string;
  parentSpanId: string | null;  // null for root spans
  conversationId: string | null; // from agent.conversation_id tag; null for non-agent spans
  startTime: string;       // ISO 8601
  durationMs: number;
  status: 'unset' | 'ok' | 'error';
  statusDescription?: string;
  kind: string;
  sourceName: string;
  tags: Record<string, string>;
}
```

### TracesPanel

Accepts `spans: SpanData[]` prop. Builds a tree structure from the flat spans array: group by `traceId`, then nest by `parentSpanId`. Renders a list of root spans (those with no `parentSpanId`) as `SpanTree` components.

`buildSpanTree(spans: SpanData[]): SpanTreeNode[]` — pure function. `SpanTreeNode` is `SpanData & { children: SpanTreeNode[] }`.

**Performance:** `buildSpanTree` must be wrapped in `useMemo` keyed on the `spans` array reference. Do not call it on every render. For incremental updates (new spans arriving via SignalR), the Zustand store appends to the flat array — `TracesPanel` recomputes the tree only when the array reference changes (Zustand slice selector ensures this).

### SpanTree and SpanNode

`SpanTree` renders a single root span as a collapsible tree. `SpanNode` renders one span: duration bar (width proportional to root span duration), status color dot, span name, duration in ms. Clicking expands `SpanDetail` below the node, showing all `tags` as a key-value table. Children render indented below.

Duration bar color: green for `ok`, red for `error`, grey for `unset`.

### Agent Selector Integration

`GET /api/agents` is fetched via TanStack Query in `useAgentsQuery`. The `Header` component renders a `Select` (shadcn/ui) populated from the agents list. The selected value is stored in `appStore.selectedAgent`. When the selected agent changes, `ChatPanel` starts a new conversation.

### MCP Queries

`useMcpQuery.ts` — TanStack Query hooks:
- `useToolsQuery()` — `GET /api/mcp/tools`, stale time 60s
- `useResourcesQuery()` — `GET /api/mcp/resources`, stale time 60s
- `usePromptsQuery()` — `GET /api/mcp/prompts`, stale time 60s
- `useInvokeTool()` — `useMutation` for `POST /api/mcp/tools/{name}/invoke`

### ToolsBrowser

Renders a two-column layout: tool list (left) + tool detail/invoker (right). Clicking a tool in the list shows its name, description, and JSON schema rendered as a formatted code block.

Below the schema: `ToolInvoker` component. A segmented control toggles between "Direct" and "Via Agent". In Direct mode: a JSON textarea pre-populated with an empty object matching the schema, a Submit button that calls `useInvokeTool()`. In Via Agent mode: same textarea but the submit routes through `useAgentHub().invokeToolViaAgent()`. After invocation, the response (or error) is shown in a syntax-highlighted JSON panel.

### ResourcesList and PromptsList

Simple list components. Each resource shows URI, name, description. Each prompt shows name, description, arguments. No invocation capability for the POC — display only.

---

## Section 12: Presentation.WebUI — Testing

### What to Build

Test infrastructure setup, feature-level unit/integration tests for chat and telemetry, and MSW handlers for all API endpoints. Target: 80% coverage.

### Test Infrastructure

`src/test/setup.ts` — imports `@testing-library/jest-dom`, starts the MSW server (`beforeAll`), resets handlers (`afterEach`), stops server (`afterAll`).

`src/test/utils.tsx` — exports `renderWithProviders(ui, options?)` that wraps the component in:
- A fresh `QueryClient` (no retries, immediate garbage collection for tests)
- `MemoryRouter`
- A mock `MsalProvider` (using `@azure/msal-react`'s test utilities or a simple mock context)
- The `ThemeProvider`

### MSW Handlers

`src/test/handlers.ts` — MSW `http` handlers for:
- `GET /api/agents` → `[{ name: 'research-agent', description: '...' }]`
- `GET /api/mcp/tools` → sample tool list
- `POST /api/mcp/tools/:name/invoke` → sample output
- `GET /api/mcp/resources` → sample resource list
- `GET /api/mcp/prompts` → sample prompt list

### SignalR Mock

`vi.mock('@microsoft/signalr')` in tests that use `useAgentHub`. The mock returns a `HubConnection` stub with `start: vi.fn()`, `stop: vi.fn()`, `on: vi.fn()`, `invoke: vi.fn()`. Tests can call the registered `on` handlers directly to simulate incoming events.

### Key Test Cases

**Chat feature:**
- `ChatInput` submits and calls `sendMessage` on the hub
- `MessageList` renders messages from the store
- `TypingIndicator` appears when `isStreaming` is true
- Simulating `TokenReceived` events updates the streaming content in the DOM
- Simulating `TurnComplete` finalizes the message

**Telemetry feature:**
- `buildSpanTree` correctly nests spans by `parentSpanId` (unit test — no rendering)
- `SpanNode` renders with green dot for `ok` status, red for `error`
- Clicking a `SpanNode` expands the tags detail panel
- `TracesPanel` with empty spans renders a placeholder message

**MCP feature:**
- `ToolsBrowser` shows tool list from MSW mock
- Clicking a tool shows its schema
- `ToolInvoker` Direct mode calls `useInvokeTool` mutation
- `ToolInvoker` Via Agent mode calls `invokeToolViaAgent`

---

## Section 13: Integration and Developer Workflow

### What to Build

`concurrently` setup, environment configuration documentation, `README` section for the new projects, and verification that `dotnet build`, `dotnet test`, `npm test`, and `npm run dev:all` all succeed.

### Vite Proxy Configuration

`vite.config.ts` server proxy:
- `/api` → `http://localhost:5001` (REST endpoints)
- `/hubs` → `http://localhost:5001` with `ws: true` (WebSocket upgrade)

This means in development the frontend makes all requests to `localhost:5173` and Vite forwards them to `localhost:5001`. No CORS issues in development. In production the two services would be on separate origins and the real CORS policy on AgentHub applies.

### package.json Scripts

```json
"scripts": {
  "dev": "vite",
  "dev:all": "concurrently -n \"API,UI\" -c \"cyan,magenta\" \"dotnet run --project ../Presentation.AgentHub\" \"vite\"",
  "build": "tsc --noEmit && vite build",
  "preview": "vite preview",
  "test": "vitest",
  "test:coverage": "vitest run --coverage",
  "test:ui": "vitest --ui"
}
```

### Environment Variables

**Two-app Azure AD model:** The correct architecture for a SPA calling a protected API uses two Azure AD app registrations:
- **API app** (`AgentHub`): exposes the `access_as_user` scope. The `AzureAd:ClientId` and `AzureAd:Audience` (`api://{apiClientId}`) in `appsettings.json` refer to this registration.
- **SPA app** (`AgentWebUI`): configured as a Single Page Application, requests the API scope `api://{apiClientId}/access_as_user`. The `VITE_AZURE_CLIENT_ID` env var refers to this registration.

`.env.example` (committed):
```
VITE_AZURE_SPA_CLIENT_ID=          # SPA app registration client ID
VITE_AZURE_TENANT_ID=              # shared tenant ID
VITE_AZURE_API_CLIENT_ID=          # API app registration client ID (for scope)
VITE_API_BASE_URL=http://localhost:5001
```

`.env.local` (gitignored): developer fills in their Azure AD app registration values.

`authConfig.ts` constructs the scope as `api://${VITE_AZURE_API_CLIENT_ID}/access_as_user`.

`AgentHub`'s `appsettings.Development.json` contains the `AzureAd` section with the **API** app's TenantId and ClientId. `appsettings.json` has placeholder values with a clear comment. Use `dotnet user-secrets` to store actual values locally.

### Azure AD App Registration Notes

The plan includes `docs/azure-ad-setup.md` explaining:
1. **Register API app** in Azure AD (single tenant). Under "Expose an API", add scope `access_as_user`. Note the Application ID URI (`api://{apiClientId}`).
2. **Register SPA app** in Azure AD. Under "Authentication", add platform "Single-page application" with redirect URI `http://localhost:5173`. Under "API permissions", add delegated permission for the API app's `access_as_user` scope.
3. Copy API app TenantId and ClientId to `appsettings.Development.json` (or user-secrets).
4. Copy SPA app ClientId, API app ClientId, and TenantId to `.env.local`.
5. (Optional) Assign `AgentHub.Traces.ReadAll` app role in the API app manifest for users who need the global traces view.

This is documentation, not code — the actual registration is a one-time manual step.

### Build Verification Steps

After all sections are implemented, the verification sequence is:
1. `dotnet build src/AgenticHarness.slnx` — zero errors, zero warnings
2. `dotnet test src/AgenticHarness.slnx` — all tests green
3. `cd src/Content/Presentation/Presentation.WebUI && npm install && npm run build` — TypeScript compiles, Vite bundles
4. `npm test` — Vitest green, coverage ≥80%
5. Manual: `npm run dev:all` — both processes start, browser loads, Azure AD login redirects correctly

---

## Implementation Order and Dependencies

Sections can be implemented in this order with no blocking dependencies between adjacent sections of the same project:

**Phase A (AgentHub foundation):**
1 → 2 → 3 → 4 → 5 → 6 → 7

**Phase B (WebUI, can start in parallel with Phase A after Section 1):**
1 → 8 → 9 → 10 → 11 → 12

**Phase C (integration):**
13 — after both phases complete

Sections 4 (Hub) and 5 (OTel Bridge) can be developed in parallel within Phase A since they have no dependency on each other (both depend on 2 and 3).

Within Phase B, Sections 10, 11, and 12 can be developed in parallel after Section 9 is done.
