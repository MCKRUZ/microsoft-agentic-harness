# Complete Specification: Presentation.AgentHub + Presentation.WebUI

Synthesized from: initial spec + codebase research + web research + interview decisions.

---

## What We're Building

Two new sibling projects added to the `microsoft-agentic-harness` solution under `src/Content/Presentation/`:

1. **`Presentation.AgentHub`** — ASP.NET Core 10 Web API + SignalR backend
2. **`Presentation.WebUI`** — Vite + React 19 + TypeScript browser client

Together they provide a browser-based interface for chatting with the AI agent pipeline and observing its real-time telemetry. The design is template-quality: enterprise auth, extensible panel architecture, Clean Architecture DI patterns, and full XML documentation.

---

## Project 1: Presentation.AgentHub

### Purpose
HTTP and WebSocket entry point to the existing agent pipeline. Replaces the `ConsoleUI` pattern for browser-based usage. Exposes chat, MCP artifacts, and real-time telemetry over SignalR.

### Location
`src/Content/Presentation/Presentation.AgentHub/`

### Technology
- .NET 10 ASP.NET Core, Minimal APIs + Controllers
- SignalR for real-time communication
- Microsoft.Identity.Web for Azure AD authentication
- OpenTelemetry `BaseExporter<Activity>` + `Channel<T>` for OTel → SignalR bridge

### Authentication
Azure Active Directory via `Microsoft.Identity.Web`. Configuration:
```json
"AzureAd": {
  "TenantId": "<tenant-id>",
  "ClientId": "<client-id>",
  "Audience": "api://<client-id>"
}
```
All endpoints protected with `[Authorize]`. SignalR hubs read token from `?access_token=` query string (standard pattern for WebSocket upgrades). `CloseOnAuthenticationExpiration = true` on SignalR.

### DI Wiring
Calls `services.GetServices(includeHealthChecksUI: true)` from `Presentation.Common` which wires the full stack (Application.Core, Infrastructure.AI, Infrastructure.AI.MCP, Infrastructure.Observability) automatically. AgentHub then adds:
- Azure AD auth services
- SignalR with hub configuration
- CORS for `http://localhost:5173` (Vite dev) + production origins
- Its own `AgentHubConfig` section bound from `AppConfig:AgentHub`
- `SignalRSpanExporter` registered as both `BaseExporter<Activity>` and `IHostedService`

### API Surface

#### REST Endpoints
| Method | Path | Purpose |
|---|---|---|
| GET | `/api/agents` | List configured agents (name, description) |
| GET | `/api/conversations` | List persisted conversations for current user |
| GET | `/api/conversations/{id}` | Get conversation history |
| DELETE | `/api/conversations/{id}` | Delete conversation |
| GET | `/api/mcp/tools` | List MCP tools with JSON schemas |
| GET | `/api/mcp/resources` | List MCP resources |
| GET | `/api/mcp/prompts` | List MCP prompts |
| POST | `/api/mcp/tools/{name}/invoke` | Invoke tool directly (IMcpToolProvider) |

#### SignalR Hub: `AgentTelemetryHub` at `/hubs/agent`

**Client → Server methods:**
```
StartConversation(agentName, conversationId)  → begins a new turn
SendMessage(conversationId, userMessage)       → triggers agent turn
InvokeToolViaAgent(conversationId, toolName, input) → routes through agent
JoinConversationGroup(conversationId)          → subscribe to conversation traces
LeaveConversationGroup(conversationId)         → unsubscribe
```

**Server → Client events:**
```
TokenReceived(conversationId, token, isComplete)     → streaming chat token
TurnComplete(conversationId, turnNumber, fullResponse) → turn finished
ToolCallStarted(conversationId, spanId, toolName, input)
ToolCallCompleted(conversationId, spanId, toolName, output, durationMs)
SpanReceived(spanData)                               → OTel span (to group)
Error(conversationId, message, code)
```

### OTel → SignalR Bridge
`SignalRSpanExporter extends BaseExporter<Activity>` + `IHostedService`:
- `Export()` writes to `Channel<SpanData>` (bounded, drop-oldest, capacity 1000) — non-blocking
- `IHostedService.StartAsync()` drains channel and calls `_hubContext.Clients.Group(traceId).SendAsync("SpanReceived", span)`
- Spans grouped by `traceId` so only subscribers of that conversation receive them
- "All Traces" subscribers join a special group `"global-traces"`

### Conversation Persistence
`IConversationStore` interface with `FileSystemConversationStore` implementation:
- Stores conversation JSON files under `AppConfig:AgentHub:ConversationsPath` (default: `./conversations/`)
- Each file: `{conversationId}.json` containing metadata + message array
- `ConversationMessage` record: `{ Role, Content, Timestamp, ToolCalls[] }`
- Wired via `AddKeyedSingleton` to match existing Infrastructure patterns

### Agent Execution Flow
1. Client calls `SendMessage(conversationId, message)` on hub
2. Hub looks up conversation history from `IConversationStore`
3. Dispatches `ExecuteAgentTurnCommand` via `IMediator`
4. As agent executes, OTel spans fire → `SignalRSpanExporter` → `SpanReceived` events to client group
5. Streaming tokens (if SK supports it) → `TokenReceived` events
6. Turn complete → save to `IConversationStore`, emit `TurnComplete`

### Configuration
New `AgentHubConfig` section:
```json
"AppConfig": {
  "AgentHub": {
    "ConversationsPath": "./conversations",
    "DefaultAgentName": "research-agent",
    "Cors": {
      "AllowedOrigins": ["http://localhost:5173"]
    }
  }
}
```

### Testing
- `WebApplicationFactory<Program>` integration tests
- Test auth: custom `TestAuthHandler` that bypasses Azure AD for tests
- SignalR hub tests: `WebApplicationFactory` + `HubConnection` against test server
- `IConversationStore` tested with temp directory fixture

---

## Project 2: Presentation.WebUI

### Purpose
Browser-based chat and observability interface. Left panel = chat. Right panel = MCP/telemetry inspector. Designed as a template for enterprise React applications.

### Location
`src/Content/Presentation/Presentation.WebUI/`

### Technology
- Vite 6 + React 19 + TypeScript 5 (strict mode)
- shadcn/ui + Tailwind CSS v4
- Zustand (UI/chat state) + TanStack Query (server state)
- `@microsoft/signalr` for real-time
- `@azure/msal-react` + `@azure/msal-browser` for Azure AD auth
- React Hook Form + Zod for forms
- `react-window` for message list virtualization
- Vitest + React Testing Library + MSW for testing

### Authentication
MSAL-based Azure AD. `MsalProvider` wraps the entire app. Protected routes use `AuthenticatedTemplate`. Token acquired via `acquireTokenSilent` and attached to:
- REST calls: `Authorization: Bearer <token>` header
- SignalR: `accessTokenFactory: () => acquireTokenSilent(...)`

Azure AD config in `src/lib/authConfig.ts`:
```typescript
export const msalConfig: Configuration = {
  auth: {
    clientId: import.meta.env.VITE_AZURE_CLIENT_ID,
    authority: `https://login.microsoftonline.com/${import.meta.env.VITE_AZURE_TENANT_ID}`,
    redirectUri: '/'
  }
};
```

Environment variables in `.env.local` (not committed):
```
VITE_AZURE_CLIENT_ID=<client-id>
VITE_AZURE_TENANT_ID=<tenant-id>
VITE_API_BASE_URL=http://localhost:5001
```

### Layout

```
┌─────────────────────────────────────────────────────────────────┐
│  AgentHub  [Agent: research-agent ▾]                   [☀/🌙] [user@] │
├──────────────────────────┬──────────────────────────────────────┤
│                          │ [My Traces][All Traces][Tools][Res][Prompts] │
│   CHAT                   │                                        │
│                          │  ← active tab content →               │
│  ┌──────────────────┐    │                                        │
│  │ user: hello      │    │                                        │
│  └──────────────────┘    │                                        │
│  ┌──────────────────┐    │                                        │
│  │ assistant: ...   │    │                                        │
│  │ ████ streaming   │    │                                        │
│  └──────────────────┘    │                                        │
│                          │                                        │
│  [________________________] [→]                                   │
└──────────────────────────┴──────────────────────────────────────┘
```

### Folder Structure

```
src/
  app/
    App.tsx              # MsalProvider + QueryClientProvider + Router
    main.tsx             # Vite entry
    providers.tsx        # All providers composed
    router.tsx           # React Router config
  components/
    layout/
      AppShell.tsx       # Header + split panel wrapper
      SplitPanel.tsx     # Resizable left/right panels
      Header.tsx         # Nav bar: agent selector, theme toggle, user menu
    ui/                  # shadcn/ui component copies (Button, Input, Badge, etc.)
    theme/
      ThemeProvider.tsx  # dark/light mode via CSS custom properties
  features/
    auth/
      LoginRedirect.tsx  # MSAL redirect handler
      useAuth.ts         # wraps useMsal for token acquisition
    chat/
      ChatPanel.tsx      # root of left panel
      MessageList.tsx    # react-window virtualized message list
      MessageItem.tsx    # single message (user/assistant/tool-call)
      ChatInput.tsx      # React Hook Form + Zod input
      TypingIndicator.tsx
      useChatStore.ts    # Zustand: messages, isStreaming, conversationId
      useChat.ts         # orchestrates SignalR + TanStack Query for chat
      types.ts
    telemetry/
      TracesPanel.tsx    # My Traces / All Traces sub-tabs + span tree
      SpanTree.tsx       # recursive span node renderer
      SpanNode.tsx       # single span with duration bar + expand
      SpanDetail.tsx     # tags/attributes side panel
      useTelemetryStore.ts # Zustand: spans keyed by traceId
      types.ts
    mcp/
      ToolsBrowser.tsx   # tool list + schema viewer
      ToolInvoker.tsx    # inline form (direct vs via-agent toggle)
      ResourcesList.tsx
      PromptsList.tsx
      useMcpQuery.ts     # TanStack Query hooks for /api/mcp/*
      types.ts
    agents/
      AgentSelector.tsx  # dropdown populated from GET /api/agents
      useAgentsQuery.ts
  hooks/
    useAgentHub.ts       # SignalR HubConnection lifecycle (useRef + state)
    useTheme.ts          # dark/light toggle persisted to localStorage
  lib/
    apiClient.ts         # axios instance with MSAL token interceptor
    queryClient.ts       # TanStack QueryClient config
    signalrClient.ts     # HubConnectionBuilder factory
    authConfig.ts        # MSAL Configuration object
  stores/
    appStore.ts          # global: selectedAgent, sidebarOpen
  types/
    api.ts               # shared API response types
    signalr.ts           # SignalR event payload types
```

### SignalR Integration

`useAgentHub` hook:
- Stores `HubConnection` in `useRef` (not state — avoids re-renders)
- Tracks `ConnectionState` in `useState`
- Registers all event handlers: `TokenReceived`, `SpanReceived`, `TurnComplete`, etc.
- Each handler dispatches to appropriate Zustand store
- Cleanup: `connection.stop()` on unmount (handles React 19 StrictMode double-invoke)
- `accessTokenFactory` calls `acquireTokenSilent` from MSAL

### Chat Streaming

1. User submits message → `useChatStore` adds optimistic message
2. Hub invokes `SendMessage(conversationId, message)`
3. `TokenReceived` events append tokens to the assistant message in Zustand store
4. `TypingIndicator` shown while `isStreaming === true`
5. `TurnComplete` sets `isStreaming = false`, saves full response

### Trace Panel Architecture

**Data model in Zustand:**
```typescript
interface TelemetryState {
  conversationSpans: Map<string, SpanData[]>;  // keyed by conversationId
  globalSpans: SpanData[];
  addSpan: (span: SpanData, conversationId?: string) => void;
}
```

**SpanTree rendering:**
- Group spans by `traceId`, build tree from `parentSpanId`
- `SpanNode` recursively renders children with indentation
- Duration bar: `width = (span.duration / rootSpan.duration) * 100%`
- Color: green=ok, red=error, grey=unset
- Click to expand `SpanDetail` with tags/attributes

### Tool Invocation Toggle

`ToolInvoker` component:
- "Direct" mode: `POST /api/mcp/tools/{name}/invoke` via TanStack Query mutation
- "Via Agent" mode: calls `SendMessage` on hub with a crafted instruction
- Toggle rendered as segmented control above the invocation form
- Response displayed in a JSON viewer panel

### vite.config.ts Proxy
```typescript
server: {
  proxy: {
    '/api': 'http://localhost:5001',
    '/hubs': { target: 'http://localhost:5001', ws: true }
  }
}
```

### package.json Scripts
```json
{
  "dev": "vite",
  "dev:all": "concurrently \"dotnet run --project ../Presentation.AgentHub\" \"vite\"",
  "build": "tsc && vite build",
  "test": "vitest",
  "test:coverage": "vitest --coverage"
}
```

### Testing
- Vitest + React Testing Library + MSW
- `src/test/setup.ts`: jsdom, MSW server setup, RTL cleanup
- `src/test/utils.tsx`: `renderWithProviders` wrapper (QueryClient, Router, MSAL mock)
- Feature tests: render component, verify SignalR event handlers update store, assert UI
- MSW handlers mock `/api/*` endpoints
- `HubConnection` mocked in tests via `vi.mock('@microsoft/signalr')`
- Coverage target: 80% (vitest --coverage --reporter=v8)

---

## Out of Scope

- WebRTC/audio input
- File upload in chat
- Multi-tenant Azure AD (single tenant assumed)
- Production deployment config (Docker, Azure App Service)
- Message threading/branching
