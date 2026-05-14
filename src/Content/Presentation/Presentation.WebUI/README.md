# Presentation.WebUI

A React + TypeScript single-page application (SPA) that provides the browser-based interface for the Agentic Harness. It connects to `Presentation.AgentHub` over SignalR for real-time agent streaming and over REST for data queries. When you open `http://localhost:5173` in development, you see a chat interface where you can select an agent, send messages, watch tokens stream in real time, browse MCP tools, and invoke them directly.

The frontend communicates via two real-time channels: SignalR (WebSocket) for the primary conversation loop and AG-UI protocol (Server-Sent Events) for standards-compliant agent streaming. Authentication uses MSAL (Microsoft Authentication Library) in production, with an automatic dev bypass when Azure AD isn't configured.

## Architecture Context

```
┌─────────────────────────────────────────────────────────────────────┐
│  Browser (React SPA)                                                │
│                                                                     │
│  ┌─────────────┐  ┌─────────────┐  ┌──────────────────────────┐   │
│  │ ChatPanel    │  │ ToolsBrowser│  │ ConversationSidebar      │   │
│  │ (messages,   │  │ (MCP tools, │  │ (history, delete, switch)│   │
│  │  streaming)  │  │  invoke)    │  │                          │   │
│  └──────┬───────┘  └──────┬─────┘  └──────────┬───────────────┘   │
│         │                  │                    │                    │
│  ┌──────┴──────────────────┴────────────────────┴──────────────┐   │
│  │  Hooks Layer                                                │   │
│  │  useAgentHub (SignalR) | useAgentStream (AG-UI/SSE)         │   │
│  │  useAgentsQuery | useMcpQuery | useConversationsQuery       │   │
│  └──────┬──────────────────┬────────────────────┬──────────────┘   │
│         │                  │                    │                    │
│  ┌──────┴──────┐    ┌─────┴─────┐    ┌────────┴────────┐          │
│  │ signalrClient│    │ agUiClient│    │ apiClient       │          │
│  │ (WebSocket)  │    │ (SSE)     │    │ (Axios + MSAL)  │          │
│  └──────┬───────┘    └─────┬─────┘    └────────┬────────┘          │
└─────────┼──────────────────┼───────────────────┼────────────────────┘
          │                  │                   │
          ▼                  ▼                   ▼
┌─────────────────────────────────────────────────────────────────────┐
│  Presentation.AgentHub (ASP.NET Core)                               │
│  /hubs/agent (SignalR) | POST /ag-ui/run (SSE) | /api/* (REST)      │
└─────────────────────────────────────────────────────────────────────┘
```

## Key Concepts

### Dual Streaming Architecture

The app supports two independent streaming protocols for agent responses:

**SignalR (Primary):** `useAgentHub` hook manages a persistent WebSocket connection. The hub emits `TokenReceived` events for each chunk and `TurnComplete` when done. Connection resilience: infinite exponential backoff reconnect, 120s server timeout, 30s keepalive.

**AG-UI (Standards-Compliant):** `useAgentStream` hook creates an SSE subscription via `@ag-ui/client`. Receives `TEXT_MESSAGE_START`, `TEXT_MESSAGE_CONTENT`, `TEXT_MESSAGE_END`, and `RUN_ERROR` events. This enables interop with any AG-UI-compatible agent backend.

Both write to the same Zustand store (`useChatStore`), so the UI is agnostic to which transport delivered the tokens.

### State Management

| Store | Library | Purpose |
|-------|---------|---------|
| `chatStore` (Zustand) | Client state | Messages, streaming content, error state |
| `appStore` (Zustand) | Client state | Selected agent, sidebar panel, active conversation |
| `conversationSettingsStore` (Zustand) | Client state | Per-conversation model/temperature overrides |
| `useAgentsQuery` (TanStack Query) | Server state | Agent list from REST API |
| `useMcpQuery` (TanStack Query) | Server state | MCP tools/resources/prompts |
| `useConversationsQuery` (TanStack Query) | Server state | Conversation history list |
| `useConfigQuery` (TanStack Query) | Server state | Auth mode, available deployments |

### Authentication

Auth state is derived from a single signal: the presence of `VITE_AZURE_SPA_CLIENT_ID` environment variable.

- **Present:** MSAL is configured, Azure AD login required, tokens attached to all API calls
- **Absent (default):** `IS_AUTH_DISABLED = true`, no login screen, no tokens needed

```typescript
// src/lib/devAuth.ts
export const IS_AUTH_DISABLED = !import.meta.env['VITE_AZURE_SPA_CLIENT_ID']
```

This eliminates the recurring "Disconnected" bug caused by missing `.env` files. No env file needed for the default development experience.

### Component Architecture

**Views** (`src/views/`) -- page-level route components (ChatView, AgentsView, ToolsView, etc.)

**Features** (`src/features/`) -- domain-specific component groups:
- `chat/` -- ChatPanel, ChatInput, MessageList, MessageItem, TypingIndicator, Markdown, CodeBlock
- `mcp/` -- ToolsBrowser, ToolInvoker, PromptsList, ResourcesList
- `agents/` -- AgentsList
- `conversations/` -- ConversationSidebar
- `commands/` -- CommandPalette

**Layout** (`src/components/layout/`) -- DashboardLayout, Header, PanelView, SidebarSwitcher

**UI primitives** (`src/components/ui/`) -- shadcn/ui components (Button, Card, Badge, Dialog, etc.)

### Real-Time Communication

The `signalrClient.ts` module builds connections with production-ready settings:

```typescript
// Infinite retry with exponential backoff (0, 2, 4, 8, 16s, then 30s+jitter)
connection.serverTimeoutInMilliseconds = 120_000;  // LLM responses can be slow
connection.keepAliveIntervalInMilliseconds = 30_000;
```

## Project Structure

```
Presentation.WebUI/
├── e2e/                              Playwright end-to-end tests
│   ├── accessibility-nesting.spec.ts
│   ├── api-routes.spec.ts
│   └── dom-integrity.spec.ts
├── src/
│   ├── app/
│   │   ├── App.tsx                   Root component
│   │   ├── providers.tsx             MSAL + QueryClient + AgentHub providers
│   │   └── router.tsx                React Router routes + auth gate
│   ├── components/
│   │   ├── layout/
│   │   │   ├── DashboardLayout.tsx   Shell with sidebar + main content
│   │   │   ├── Header.tsx            Top bar with agent selector
│   │   │   ├── PanelView.tsx         Split panel container
│   │   │   └── SidebarSwitcher.tsx   Navigation sidebar
│   │   ├── theme/
│   │   │   └── ThemeProvider.tsx     Dark/light mode
│   │   └── ui/                       shadcn/ui primitives (20+ components)
│   ├── features/
│   │   ├── agents/
│   │   │   ├── AgentsList.tsx        Agent cards with selection
│   │   │   └── useAgentsQuery.ts     GET /api/agents
│   │   ├── chat/
│   │   │   ├── ChatPanel.tsx         Main chat container
│   │   │   ├── ChatInput.tsx         Message input with mentions
│   │   │   ├── MessageList.tsx       Virtualized message rendering
│   │   │   ├── MessageItem.tsx       Individual message (markdown + tools)
│   │   │   ├── Markdown.tsx          react-markdown with syntax highlighting
│   │   │   ├── CodeBlock.tsx         Fenced code with copy button
│   │   │   ├── MentionPicker.tsx     @-mention agent/tool autocomplete
│   │   │   ├── TypingIndicator.tsx   Animated dots during streaming
│   │   │   ├── ConversationSettingsDrawer.tsx  Model/temperature/prompt config
│   │   │   ├── useChatStore.ts       Zustand store (messages, streaming state)
│   │   │   └── __tests__/            Unit tests for chat components
│   │   ├── mcp/
│   │   │   ├── ToolsBrowser.tsx      Tool list with search + schema display
│   │   │   ├── ToolInvoker.tsx       Direct tool invocation form
│   │   │   ├── PromptsList.tsx       MCP prompt templates
│   │   │   ├── ResourcesList.tsx     MCP resource listing
│   │   │   └── useMcpQuery.ts        GET /api/mcp/tools|resources|prompts
│   │   ├── conversations/
│   │   │   ├── ConversationSidebar.tsx  History list with delete
│   │   │   ├── useConversationsQuery.ts
│   │   │   └── useDeleteConversation.ts
│   │   ├── commands/
│   │   │   └── CommandPalette.tsx    Cmd+K command palette
│   │   └── config/
│   │       └── useConfigQuery.ts     GET /api/config/auth
│   ├── hooks/
│   │   ├── useAgentHub.tsx           SignalR connection + hub methods (provider)
│   │   ├── useAgentStream.ts         AG-UI SSE streaming hook
│   │   ├── useAuth.ts               MSAL account + token (dev-aware)
│   │   └── useTheme.ts              Dark/light mode hook
│   ├── lib/
│   │   ├── agUiClient.ts            AG-UI HttpAgent factory
│   │   ├── apiClient.ts             Axios instance with MSAL interceptor
│   │   ├── authConfig.ts            MSAL PublicClientApplication config
│   │   ├── browserLogger.ts         Frontend → backend log shipping
│   │   ├── devAuth.ts               IS_AUTH_DISABLED flag + DEV_ACCOUNT
│   │   ├── navigation.tsx           Route definitions
│   │   ├── queryClient.ts           TanStack Query configuration
│   │   ├── signalrClient.ts         SignalR HubConnection builder
│   │   └── utils.ts                 cn() class merging utility
│   ├── stores/
│   │   ├── appStore.ts              UI state (selected agent, panel)
│   │   ├── chatStore.ts             Re-export from features/chat
│   │   └── conversationSettingsStore.ts  Per-conversation overrides
│   ├── types/
│   │   └── api.ts                   Shared API response types
│   ├── views/
│   │   ├── ChatView.tsx             /chat route
│   │   ├── AgentsView.tsx           /agents route
│   │   ├── ToolsView.tsx            /tools route
│   │   ├── ResourcesView.tsx        /resources route
│   │   └── PromptsView.tsx          /prompts route
│   ├── test/
│   │   ├── setup.ts                 Vitest + MSW bootstrap
│   │   ├── handlers.ts              MSW request handlers
│   │   └── utils.tsx                Test render wrapper with providers
│   ├── main.tsx                     Vite entry point
│   └── index.css                    Tailwind CSS imports
├── components.json                  shadcn/ui configuration
├── playwright.config.ts             E2E test configuration
├── tsconfig.json                    TypeScript config (paths, strict)
├── tsconfig.app.json                App-specific TS config
├── tsconfig.test.json               Test-specific TS config
├── eslint.config.js                 ESLint 9 flat config
├── index.html                       Vite HTML entry
└── package.json                     Dependencies + scripts
```

## Key Components Reference

| Component | Location | Purpose |
|-----------|----------|---------|
| `ChatPanel` | features/chat/ | Main chat container, orchestrates send/stream/display |
| `useAgentHub` | hooks/ | SignalR lifecycle, hub method wrappers (provider pattern) |
| `useAgentStream` | hooks/ | AG-UI SSE subscription + event handling |
| `useChatStore` | features/chat/ | Messages array, streaming state, error state |
| `signalrClient` | lib/ | HubConnection factory with resilience config |
| `agUiClient` | lib/ | AG-UI HttpAgent factory with auth |
| `apiClient` | lib/ | Axios with MSAL token interceptor |
| `DashboardLayout` | components/layout/ | App shell (sidebar + outlet) |
| `ToolsBrowser` | features/mcp/ | MCP tool list with schema viewer |

## Configuration (Environment Variables)

Create `.env.local` for local overrides (gitignored):

```bash
# Required for Azure AD (omit for dev bypass)
VITE_AZURE_SPA_CLIENT_ID=your-spa-client-id
VITE_AZURE_TENANT_ID=your-tenant-id

# Optional
VITE_API_BASE_URL=https://localhost:52001  # Only if backend runs on non-default port
```

**No `.env` file needed for default development.** When `VITE_AZURE_SPA_CLIENT_ID` is absent, auth is disabled automatically on both client and server.

### Vite Proxy Configuration

In development, Vite proxies API and SignalR requests to the AgentHub backend. The proxy is configured in `vite.config.ts`:
- `/api/*` -- proxied to `http://localhost:52000`
- `/hubs/*` -- proxied to `http://localhost:52000` (WebSocket upgrade)
- `/ag-ui/*` -- proxied to `http://localhost:52000`

## How to Run

```bash
# Prerequisites: Node.js 20+, npm

# Install dependencies
npm install

# Development (requires AgentHub running on :52000)
npm run dev
# Opens http://localhost:5173

# Or run both together:
npm run dev:all
# Starts AgentHub (dotnet run) + Vite (npm run dev) concurrently

# Production build
npm run build
# Output: dist/ (served by AgentHub in Release mode)

# Type check only (no emit)
npx tsc --noEmit
```

### With AgentHub (recommended development flow)

```bash
# Terminal 1: AgentHub backend (auto-launches Vite via SPA proxy)
dotnet run --project src/Content/Presentation/Presentation.AgentHub

# Browser: http://localhost:5173 (Vite) or http://localhost:52000 (Kestrel)
```

## Common Tasks

### Adding a New Page/View

1. Create `src/views/MyView.tsx`
2. Add route in `src/app/router.tsx`
3. Add navigation entry in `src/lib/navigation.tsx`
4. Add sidebar icon in `SidebarSwitcher.tsx`

### Adding a New API Hook

1. Create `src/features/myFeature/useMyQuery.ts`
2. Use TanStack Query: `useQuery({ queryKey: [...], queryFn: () => apiClient.get(...) })`
3. Export and consume in your view/component

### Adding a New UI Component

```bash
npx shadcn add button  # Installs from shadcn/ui registry
```

Components are placed in `src/components/ui/` with Tailwind CSS styling.

### Debugging SignalR Connection Issues

1. Check browser console for connection state logs
2. Verify AgentHub is running on `:52000`
3. Check auth mismatch warning in console (client vs server auth mode)
4. For WebSocket issues, check that `/hubs/agent` returns 101 Switching Protocols

## Dependencies

### Production Dependencies

| Package | Purpose |
|---------|---------|
| `react` / `react-dom` 19 | UI framework |
| `@microsoft/signalr` 10 | Real-time WebSocket communication |
| `@ag-ui/client` / `@ag-ui/core` | AG-UI protocol SSE streaming |
| `@azure/msal-browser` / `@azure/msal-react` | Azure AD authentication |
| `@tanstack/react-query` | Server state management |
| `zustand` | Client state management |
| `axios` | HTTP client with interceptors |
| `react-router-dom` 7 | Client-side routing |
| `tailwindcss` 4 | Utility-first CSS |
| `react-markdown` / `rehype-highlight` | Markdown rendering with syntax highlighting |
| `lucide-react` | Icon library |
| `zod` | Runtime validation (forms) |
| `react-hook-form` | Form state management |
| `rxjs` | Observable streams (AG-UI) |

### Dev Dependencies

| Package | Purpose |
|---------|---------|
| `vite` 8 | Build tool + dev server |
| `vitest` 4 | Unit test framework |
| `@playwright/test` | End-to-end testing |
| `@testing-library/react` | Component testing |
| `msw` 2 | API mocking (tests) |
| `typescript` 6 | Type checking |
| `eslint` 9 | Linting |

## Testing

### Unit Tests (Vitest)

```bash
npm run test           # Watch mode
npm run test -- --run  # Single pass (CI)
npm run test:coverage  # With V8 coverage report
npm run test:ui        # Vitest UI browser
```

Test files: `src/**/__tests__/*.test.{ts,tsx}`

Uses MSW (Mock Service Worker) for API mocking and `@testing-library/react` for component rendering. Test utilities in `src/test/utils.tsx` wrap components with all required providers.

### End-to-End Tests (Playwright)

```bash
npm run test:e2e       # Headless
npm run test:e2e:ui    # Interactive Playwright UI
```

Test files: `e2e/*.spec.ts`

Covers: DOM integrity, accessibility nesting, API route availability.

### What Tests Cover

- Chat message rendering and streaming state transitions
- SignalR connection lifecycle (connect, reconnect, disconnect)
- Auth disabled/enabled mode switching
- API client token attachment
- Component rendering with mock data
- Theme provider dark/light switching
