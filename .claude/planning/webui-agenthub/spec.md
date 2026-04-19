# Spec: Presentation.AgentHub + Presentation.WebUI

## Overview

Add two new presentation-layer projects to the `microsoft-agentic-harness` solution:
a .NET Web API backend (`Presentation.AgentHub`) and a React/TypeScript frontend (`Presentation.WebUI`).
Together they provide a browser-based chat interface with a real-time MCP/telemetry panel.

---

## Project 1: Presentation.AgentHub (.NET ASP.NET Core Web API)

**Location:** `src/Content/Presentation/Presentation.AgentHub/`

### Responsibilities
- Exposes a SignalR Hub (`AgentTelemetryHub`) for real-time telemetry streaming to the browser
- Exposes a streaming chat endpoint that wires to the existing `Application.Core` agent pipeline for real AI calls
- Exposes MCP artifact endpoints:
  - `GET /api/mcp/tools` — list tools with schemas
  - `GET /api/mcp/resources` — list resources
  - `GET /api/mcp/prompts` — list prompts
  - `POST /api/mcp/tools/{name}/invoke` — invoke a tool directly
- Bridges OpenTelemetry events to SignalR broadcasts (OTel → SignalR telemetry events)
- JWT authentication on all endpoints
- CORS configured for Vite dev server (`localhost:5173`)
- Should be added to `AgenticHarness.slnx`

### Architecture Constraints
- Follows existing Clean Architecture DI patterns (each layer has `DependencyInjection.cs` with `Add*Dependencies()`)
- References `Presentation.Common` for shared middleware, security, OpenTelemetry extensions
- References `Application.Core` for CQRS commands (agent orchestration pipeline)
- References `Infrastructure.AI.MCP` for MCP server/client infrastructure
- References `Infrastructure.Observability` for OpenTelemetry
- References `Infrastructure.AI` for agent execution
- Full XML documentation on all public types (this is a template project)
- `appsettings.json` follows existing `AppConfig` hierarchy

### Key Components
- `Program.cs` — minimal hosting, DI wiring, middleware pipeline
- `Hubs/AgentTelemetryHub.cs` — SignalR hub for telemetry + chat streaming
- `Controllers/ChatController.cs` — chat endpoint, invokes MediatR commands
- `Controllers/McpController.cs` — MCP artifact endpoints
- `Services/TelemetryBroadcastService.cs` — OTel span listener → SignalR broadcast
- `DependencyInjection.cs` — registers all AgentHub services

---

## Project 2: Presentation.WebUI (React + TypeScript)

**Location:** `src/Content/Presentation/Presentation.WebUI/`

### Tech Stack
- **Build:** Vite + React 19 + TypeScript (strict mode)
- **UI:** shadcn/ui + Tailwind CSS v4
- **State:** Zustand (UI/chat state) + TanStack Query (server state/cache)
- **Real-time:** `@microsoft/signalr` client
- **Forms:** React Hook Form + Zod
- **Testing:** Vitest + React Testing Library + MSW
- **Ports:** Vite dev server on `5173`, proxy `/api/*` and `/hubs/*` → `localhost:5001`

### Layout
Split-panel layout:
- **Left panel — Chat:**
  - Message list with streaming token rendering (react-window for virtualization)
  - Typing indicator
  - Input form (React Hook Form + Zod, `min(1)` / `max(4000)` validation)
  - Connects to AgentHub via SignalR for streaming responses
- **Right panel — Tabbed MCP/Telemetry:**
  - **Traces tab:** Real-time nested span tree visualization (OTel-style), fed by SignalR
  - **Tools tab:** Browse MCP tools with JSON schema viewer + inline invocation form
  - **Resources tab:** Browse MCP server resources
  - **Prompts tab:** Browse MCP server prompts
- Dark/light theme toggle

### Folder Structure (bulletproof-react / feature-based)
```
src/
  app/              # providers, router, main app shell (App.tsx, main.tsx)
  components/       # shared UI (SplitPanel, ThemeToggle, etc.)
  features/
    chat/           # ChatPanel, MessageList, ChatInput, useChatStore, types
    telemetry/      # TracePanel, SpanTree, SpanNode, useTelemetryStore, types
    mcp/            # ToolsBrowser, ToolInvoker, ResourcesList, PromptsList, types
  hooks/            # useAgentHub (SignalR), useAuth
  lib/              # apiClient, queryClient, signalrClient setup
  stores/           # global Zustand stores (app-level)
  types/            # global TypeScript types (AgentEvent, McpTool, etc.)
```

### Key Constraints
- JWT: WebUI must obtain a token and attach it to both SignalR handshake and REST calls
- Real-time: SignalR `useAgentHub` hook manages connection lifecycle (connect/reconnect/disconnect)
- Streaming chat: assistant tokens arrive via SignalR events and are appended incrementally
- Traces: OTel span events arrive via SignalR and build a nested span tree in real-time
- 80% test coverage on new code
- Features must NOT import from each other — compose at app layer only

---

## Dev Workflow

- AgentHub runs on port `5001`
- Vite dev server runs on port `5173`
- Vite proxy config forwards `/api/*` and `/hubs/*` to `http://localhost:5001`
- `package.json` includes a `dev:all` script using `concurrently` to start both simultaneously

---

## Confirmed Decisions

| Decision | Choice |
|---|---|
| Auth | JWT (full, not dev-only stub) |
| Agent wiring | Real AI calls through existing `Application.Core` pipeline |
| Ports | AgentHub=5001, Vite=5173 |
| UI library | shadcn/ui + Tailwind (default) |
| Real-time | SignalR (`@microsoft/signalr`) |
| Streaming | SignalR hub events for both chat tokens and telemetry |
