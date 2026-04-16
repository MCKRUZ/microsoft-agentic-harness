# Presentation.WebUI

React + TypeScript SPA that provides a browser-based interface for the Agentic Harness. It connects to `Presentation.AgentHub` over SignalR for real-time agent streaming and over REST for data queries.

## Features

- **Chat panel** — sends messages to the selected agent, streams tokens in real time via SignalR, and displays the full conversation history with tool call summaries
- **Telemetry panel** — receives OpenTelemetry spans from the agent over SignalR and renders them as a live, collapsible span tree for in-browser trace visualization
- **MCP browser** — lists tools, prompts, and resources exposed by the AgentHub's MCP server; supports direct tool invocation and invocation via the agent

## Stack

| | |
|---|---|
| **Framework** | React 19, TypeScript, Vite |
| **Styling** | Tailwind CSS, shadcn/ui |
| **State** | Zustand (client state), TanStack Query (server state) |
| **Real-time** | `@microsoft/signalr` → `Presentation.AgentHub` |
| **Auth** | MSAL (`@azure/msal-react`) + dev bypass (`VITE_AUTH_DISABLED=true`) |
| **Testing** | Vitest, React Testing Library, MSW |

## Getting Started

```bash
npm install
cp .env.example .env.local   # fill in VITE_* values
npm run dev                  # http://localhost:5173
```

For local development without Azure AD:

```bash
# .env.local
VITE_AUTH_DISABLED=true
VITE_API_BASE_URL=https://localhost:7080
```

Also set `Auth:Disabled=true` in `Presentation.AgentHub/appsettings.Development.json`. See `docs/azure-ad-setup.md` for full Azure AD configuration.

## Commands

```bash
npm run dev          # dev server with HMR
npm run build        # production build (tsc --noEmit + vite build)
npm run test         # Vitest (watch mode)
npm run test:run     # Vitest (single pass, CI)
npm run lint         # ESLint
```

## Project Structure

```
src/
├── app/                  App entry, MSAL + QueryClient providers
├── components/
│   ├── layout/           AppShell, Header
│   └── ui/               shadcn/ui primitives (Button, Card, Badge, …)
├── features/
│   ├── agents/           useAgentsQuery — agent list from REST
│   ├── chat/             ChatPanel, MessageList, ChatInput, useChatStore
│   ├── mcp/              ToolsBrowser, ToolInvoker, PromptsList, ResourcesList
│   └── telemetry/        TracesPanel, SpanTree, SpanNode, buildSpanTree
├── hooks/
│   ├── useAgentHub.ts    SignalR connection lifecycle + hub methods
│   └── useAuth.ts        MSAL account + token acquisition (dev-bypass aware)
├── lib/
│   ├── apiClient.ts      Axios instance with MSAL token interceptor
│   ├── authConfig.ts     MSAL PublicClientApplication config
│   └── devAuth.ts        IS_AUTH_DISABLED flag + DEV_ACCOUNT synthetic account
├── stores/
│   ├── appStore.ts       Selected agent, panel layout
│   ├── chatStore.ts      → re-exports from features/chat/useChatStore
│   └── telemetryStore.ts Conversation + global span state (capped at 200/500)
└── test/
    ├── setup.ts          Vitest + MSW bootstrap
    └── handlers.ts       MSW request handlers for tests
```
