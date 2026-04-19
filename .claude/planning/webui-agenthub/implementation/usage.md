# Usage Guide — WebUI AgentHub

Generated from the implementation of sections 01–13.

---

## Prerequisites

- .NET 10 SDK
- Node.js 20+
- Two Azure AD app registrations (see `docs/azure-ad-setup.md`)

---

## Quick Start

### 1. Configure Azure AD

```bash
# Backend (from Presentation.AgentHub directory)
dotnet user-secrets set "AzureAd:TenantId"  "<tenant-id>"
dotnet user-secrets set "AzureAd:ClientId"  "<api-app-client-id>"
dotnet user-secrets set "AzureAd:Audience"  "api://<api-app-client-id>"
```

```bash
# Frontend (from Presentation.WebUI directory)
cp .env.example .env.local   # or: Copy-Item .env.example .env.local (PowerShell)
# Fill in VITE_AZURE_SPA_CLIENT_ID, VITE_AZURE_TENANT_ID, VITE_AZURE_API_CLIENT_ID
```

### 2. Start the full stack

```bash
cd src/Content/Presentation/Presentation.WebUI
npm install
npm run dev:all
```

This starts:
- **AgentHub** on `http://localhost:5001` (ASP.NET Core + SignalR + MCP)
- **Vite dev server** on `http://localhost:5173` (React SPA, proxies `/api/*` and `/hubs/*`)

Open `http://localhost:5173` — MSAL redirects to Azure AD and returns with a token.

---

## Running Tests

### Backend (.NET)
```bash
dotnet test src/AgenticHarness.slnx
dotnet test src/AgenticHarness.slnx --collect:"XPlat Code Coverage"
```

### Frontend (Vitest)
```bash
cd src/Content/Presentation/Presentation.WebUI
npm test                  # run all tests once
npm run test:watch        # watch mode
npm run test:coverage     # with coverage report
npm run test:ui           # Vitest UI browser
```

### Build verification
```bash
dotnet build src/AgenticHarness.slnx     # must exit 0
cd src/Content/Presentation/Presentation.WebUI
npm run build                            # tsc --noEmit + vite bundle
```

---

## API Reference

### AgentHub REST Endpoints (`/api/`)

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/agents` | List available agents |
| POST | `/api/agents/{id}/conversations` | Start a new conversation |
| GET | `/api/agents/{id}/conversations` | List conversations for an agent |
| POST | `/api/agents/{id}/conversations/{convId}/messages` | Send a message |

### MCP Endpoints (`/api/mcp/`)

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/mcp/tools` | List available MCP tools |
| POST | `/api/mcp/tools/{name}/execute` | Execute a tool |
| GET | `/api/mcp/resources` | List MCP resources |
| GET | `/api/mcp/prompts` | List MCP prompts |

### SignalR Hubs

| Hub | Path | Events |
|-----|------|--------|
| `AgentTelemetryHub` | `/hubs/telemetry` | `ReceiveSpan`, `ReceiveMetric` |
| Conversation streaming | `/hubs/conversation` | `ReceiveChunk`, `ReceiveComplete` |

---

## Project Structure

```
src/Content/
├── Presentation/
│   ├── Presentation.AgentHub/        # ASP.NET Core host — REST API + SignalR + MCP
│   └── Presentation.WebUI/           # React 19 + Vite 8 SPA
│       ├── src/
│       │   ├── components/           # UI components (chat, telemetry, MCP browser)
│       │   ├── features/             # Feature panels (ChatFeaturePanel, TelemetryPanel)
│       │   ├── lib/                  # API client, SignalR hooks, auth config
│       │   └── test/                 # MSW handlers, Vitest utilities
│       ├── .env.example              # Required env vars (copy to .env.local)
│       └── vite.config.ts            # Proxy: /api + /hubs -> :5001
└── Tests/
    ├── Presentation.AgentHub.Tests/  # xUnit integration tests
    └── (other layer tests)
docs/
└── azure-ad-setup.md                 # Two-app Azure AD registration guide
```

---

## Architecture Notes

- **Two-app Azure AD model**: SPA (`AgentWebUI`) acquires tokens for the API (`AgentHub`) using the `access_as_user` delegated scope. `authConfig.ts` constructs the scope as `api://{VITE_AZURE_API_CLIENT_ID}/access_as_user`.
- **Vite proxy**: In development, all `/api/*` and `/hubs/*` requests are forwarded to `http://localhost:5001` with `changeOrigin: true`. No CORS configuration is needed in development.
- **Streaming**: Chat responses stream via SignalR chunks. Telemetry spans are pushed from the OTel bridge (`SignalRSpanExporter`) through `AgentTelemetryHub`.
- **MCP**: The backend exposes tools/prompts/resources via the Model Context Protocol API. The frontend MCP browser (`section-11`) provides live inspection.
