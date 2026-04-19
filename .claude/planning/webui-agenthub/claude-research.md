# Research Findings: Presentation.AgentHub + Presentation.WebUI

Compiled from: codebase exploration, React best practices research, MCP UI patterns, SignalR+JWT+OTel web research.

---

## Part 1: Existing Codebase Patterns

### 1.1 Application.Core — CQRS Commands

Available MediatR commands for the chat endpoint to dispatch:

| Command | Purpose | Timeout |
|---|---|---|
| `ExecuteAgentTurnCommand` | Single agent turn with tool chain support | 5 min |
| `RunConversationCommand` | Multi-turn conversation | 10 min |
| `RunOrchestratedTaskCommand` | Multi-agent orchestration | Varies |

All commands implement `IAgentScopedRequest` (sets `AgentId`, `ConversationId`, `TurnNumber`) and are record types with init-only properties.

Handlers auto-discovered via:
```csharp
services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(assembly));
services.AddValidatorsFromAssembly(assembly);
```

### 1.2 Presentation.Common — Single DI Entry Point

**Critical pattern** — the entire DI graph is wired by one call:
```csharp
services.GetServices(includeHealthChecksUI: true);  // Web API
services.GetServices(includeHealthChecksUI: false); // Console/Worker
```

**Registration order (enforced internally):**
1. Configuration binding (`RegisterConfigSections()`)
2. Application.Common (MediatR, FluentValidation)
3. Application.AI.Common (AI pipeline behaviors)
4. Infrastructure.Common (Identity, auth)
5. Infrastructure.AI (Tools, state, file system)
6. Infrastructure.AI.Connectors (External APIs)
7. Infrastructure.AI.MCP (MCP client)
8. Infrastructure.APIAccess (HTTP resilience)
9. Infrastructure.Observability (OTel — MUST be last: configures the pipeline after all ITelemetryConfigurator registrations)

AgentHub must call `GetServices(includeHealthChecksUI: true)` then ADD its own services on top (SignalR, CORS, JWT, controllers).

### 1.3 Infrastructure.AI.MCP — MCP Interfaces

```csharp
// Tool discovery
IMcpToolProvider         // Discovers tools from McpConnectionManager
IMcpResourceProvider     // Exposes resources (e.g. trace:// URIs)
McpConnectionManager     // Singleton managing MCP client lifecycles
```

MCP tools/resources are already discoverable from these interfaces — the `McpController` just needs to call them.

### 1.4 Infrastructure.Observability — OTel Pipeline

Key AppContext switches required at startup:
```csharp
AppContext.SetSwitch("Microsoft.SemanticKernel.Experimental.GenAI.EnableOTelDiagnostics", true);
AppContext.SetSwitch("Azure.Experimental.EnableActivitySource", true);
```

`ITelemetryConfigurator` is the extensibility point — implementations registered before `AddOpenTelemetry()` builds the pipeline. AgentHub's `SignalRSpanExporter` should implement this interface.

### 1.5 AppConfig Hierarchy

```json
{
  "AppConfig": {
    "Agent": { "MaxTurnsPerConversation": 10 },
    "AI": { "AgentFramework": {...}, "McpServers": {...} },
    "Observability": { "EnableTracing": true, "SamplingRatio": 1.0 },
    "Infrastructure": { "FileSystem": { "AllowedBasePaths": [...] } }
  }
}
```

AgentHub adds its own config section under `AppConfig:AgentHub` (JWT keys, CORS origins, SignalR settings).

### 1.6 ConsoleUI Entry Pattern (reference)

```csharp
var mediator = serviceProvider.GetRequiredService<IMediator>();
var result = await mediator.Send(new ExecuteAgentTurnCommand
{
    AgentName = "research-agent",
    UserMessage = "...",
    ConversationHistory = []
});
```

AgentHub's `ChatController` replaces this with HTTP/SignalR entry points.

---

## Part 2: React + TypeScript Best Practices (2025)

Source: bulletproof-react (34.8k stars), TanStack ecosystem, community consensus.

### 2.1 Stack

| Concern | Choice | Rationale |
|---|---|---|
| Build | Vite | Sub-second HMR, ESM-native, CRA is abandoned |
| UI components | shadcn/ui + Tailwind CSS v4 | Copy-paste, no lock-in, you own the code |
| State (UI) | Zustand | Lightweight, <5KB, minimal boilerplate |
| State (server) | TanStack Query | Dedup, stale-while-revalidate, offline |
| Real-time | @microsoft/signalr | Matches .NET backend, bidirectional |
| Forms | React Hook Form + Zod | Standard pairing, zodResolver |
| Testing | Vitest + RTL + MSW | 10x faster than Jest, Vite-native |

### 2.2 Feature-Based Folder Structure (bulletproof-react)

```
src/
  app/              # providers, router, App.tsx, main.tsx
  components/       # shared UI (SplitPanel, ThemeToggle, Spinner)
  features/
    chat/           # ChatPanel, MessageList, ChatInput, useChatStore, types
    telemetry/      # TracePanel, SpanTree, SpanNode, useTelemetryStore, types
    mcp/            # ToolsBrowser, ToolInvoker, ResourcesList, PromptsList, types
  hooks/            # shared hooks (useAgentHub, useAuth)
  lib/              # apiClient, queryClient, signalrClient
  stores/           # global Zustand stores
  types/            # global TypeScript types
```

**Rule:** Features must NOT import from each other. Compose at `app/` layer only.

### 2.3 TypeScript Config (strict)

```json
{
  "compilerOptions": {
    "strict": true,
    "noUncheckedIndexedAccess": true,
    "noImplicitReturns": true,
    "baseUrl": ".",
    "paths": { "@/*": ["src/*"] }
  }
}
```

### 2.4 Chat UI Patterns

- Message list: `react-window` for virtualization (handles 1000s of messages)
- Streaming tokens: append to Zustand store on each SignalR `TokenReceived` event
- Typing indicator: CSS animation, shown when `isStreaming === true`
- Accessibility: `role="log"` on feed, `aria-live="polite"`, `aria-label` on input
- Optimistic updates: TanStack Query `onMutate` → message appears immediately

---

## Part 3: MCP Inspector UI Patterns

Source: @modelcontextprotocol/inspector repo + LangSmith/LangFuse observability patterns.

### 3.1 OTel Span Data Model

```typescript
interface SpanData {
  traceId: string;
  spanId: string;
  parentSpanId: string | null;
  name: string;
  startTime: string;        // ISO 8601
  duration: number;         // milliseconds
  status: 'unset' | 'ok' | 'error';
  statusDescription?: string;
  kind: string;             // 'internal' | 'client' | 'server'
  sourceName: string;       // ActivitySource name
  tags: Record<string, string>;
}

interface AgentTurnTrace {
  traceId: string;
  turnNumber: number;
  startTime: string;
  spans: SpanData[];        // nested by parentSpanId on client
}
```

### 3.2 Extensible Provider Pattern

Structure the MCP panel to accept pluggable providers:
- `OTelProvider` — OpenTelemetry traces from SignalR
- `MCPArtifactProvider` — Tool/resource/prompt metadata from REST API
- `LocalTraceProvider` — JSON file ingestion (future)

### 3.3 Trace Visualization

- Build span tree on client: group by `traceId`, nest by `parentSpanId`
- Display as collapsible tree with duration bars (CSS width = duration/totalDuration)
- Color-code by status: green=ok, red=error, grey=unset
- Click span to expand tags/attributes in a detail panel

---

## Part 4: SignalR + React 19 Integration

Source: Microsoft Docs (ASP.NET Core SignalR JavaScript client, .NET 10).

### 4.1 HubConnectionBuilder with JWT

```typescript
const connection = new HubConnectionBuilder()
  .withUrl('/hubs/agent', {
    accessTokenFactory: () => authStore.getState().token ?? ''
  })
  .withAutomaticReconnect([0, 2000, 10000, 30000])
  .configureLogging(LogLevel.Warning)
  .build();
```

**Critical:** Browsers cannot set `Authorization` header for WebSocket upgrades. Token travels as `?access_token=` query string. Server must extract it in `OnMessageReceived`.

### 4.2 React Hook Pattern

```typescript
// useAgentHub.ts — connection stored in useRef (not useState) to avoid re-renders
export function useAgentHub() {
  const connectionRef = useRef<HubConnection | null>(null);
  const [state, setState] = useState<'disconnected'|'connecting'|'connected'|'reconnecting'>('disconnected');

  useEffect(() => {
    const conn = buildConnection();  // factory fn
    conn.onreconnecting(() => setState('reconnecting'));
    conn.onreconnected(() => setState('connected'));
    conn.onclose(() => setState('disconnected'));
    connectionRef.current = conn;
    setState('connecting');
    conn.start().then(() => setState('connected')).catch(() => setState('disconnected'));
    return () => { conn.stop(); };  // React 19 StrictMode double-invoke handled by cleanup
  }, []);

  return { connection: connectionRef.current, state };
}
```

**React 19 Strict Mode:** double-invokes `useEffect` in dev. Cleanup `conn.stop()` handles this correctly.

### 4.3 SignalR Hub Events (server → client)

| Event | Payload | Purpose |
|---|---|---|
| `TokenReceived` | `{ token: string, isComplete: bool }` | Streaming chat token |
| `SpanReceived` | `SpanData` | OTel span broadcast |
| `TurnComplete` | `{ conversationId, turnNumber }` | Chat turn finished |
| `ToolCallStarted` | `{ spanId, toolName, input }` | Tool execution began |
| `ToolCallCompleted` | `{ spanId, toolName, output, durationMs }` | Tool execution done |
| `Error` | `{ message, code }` | Pipeline error |

---

## Part 5: JWT in ASP.NET Core 10

Source: Microsoft Docs (Minimal API auth, SignalR auth, dotnet user-jwts).

### 5.1 AddAuthentication + AddJwtBearer

```csharp
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.Zero,
            ValidIssuer = config["AppConfig:AgentHub:Jwt:Issuer"],
            ValidAudience = config["AppConfig:AgentHub:Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(config["AppConfig:AgentHub:Jwt:Key"]!))
        };
        // SignalR: extract token from query string
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                var token = ctx.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token) && ctx.Request.Path.StartsWithSegments("/hubs"))
                    ctx.Token = token;
                return Task.CompletedTask;
            }
        };
    });
```

### 5.2 Dev Token Generation

```bash
dotnet user-jwts create --role "developer" --claim "sub=dev-user"
```

Writes signing key to `appsettings.Development.json` automatically. No manual key management in dev.

### 5.3 Token Endpoint (for WebUI to obtain tokens)

Simple `POST /auth/token` endpoint that accepts credentials and issues a JWT. For POC, username/password can be hardcoded or from config. Marked `[AllowAnonymous]`.

### 5.4 CloseOnAuthenticationExpiration

```csharp
builder.Services.AddSignalR(opts => opts.CloseOnAuthenticationExpiration = true);
```

Closes WebSocket when JWT expires rather than letting stale connections persist.

---

## Part 6: OTel → SignalR Bridge

Source: Microsoft Docs (OTel .NET collection walkthroughs), OpenTelemetry SDK docs.

### 6.1 Recommended Pattern: BaseExporter<Activity>

Integrates into OTel pipeline (sampling and processors run first):

```csharp
public class SignalRSpanExporter : BaseExporter<Activity>
{
    private readonly IHubContext<AgentTelemetryHub> _hubContext;
    private readonly Channel<SpanData> _channel =
        Channel.CreateBounded<SpanData>(new BoundedChannelOptions(1000)
        { FullMode = BoundedChannelFullMode.DropOldest });

    public override ExportResult Export(in Batch<Activity> batch)
    {
        foreach (var activity in batch)
            _channel.Writer.TryWrite(MapToSpanData(activity)); // non-blocking
        return ExportResult.Success;
    }
    // IHostedService drains channel and calls SendAsync
}
```

**Thread safety:** `IHubContext<T>` is singleton and `SendAsync` is thread-safe. Use fire-and-forget or `Channel<T>` for backpressure.

**Registration:** Must be done AFTER `app.Build()` when `IHubContext` is available. Register as both `BaseExporter<Activity>` and `IHostedService`.

### 6.2 Key Activity Fields to Extract

```csharp
activity.OperationName      // span name
activity.TraceId            // W3C trace ID
activity.SpanId             // this span
activity.ParentSpanId       // for tree building
activity.StartTimeUtc       // absolute start
activity.Duration           // set after ActivityStopped
activity.Status             // Unset | Ok | Error
activity.StatusDescription  // error message
activity.Tags               // IEnumerable<KVP<string,string?>>
activity.Kind               // Client/Server/Internal
activity.Source.Name        // instrumentation library name
```

---

## Part 7: Testing Setup

### .NET (AgentHub)
- xUnit + `WebApplicationFactory<Program>` for integration tests
- JWT test token: use `dotnet user-jwts` or inject test auth handler
- SignalR integration tests: `WebApplicationFactory` + `HubConnection` pointed at test server
- Existing pattern: in-memory DB, real MediatR pipeline

### React (WebUI)
- Vitest + React Testing Library + MSW
- `msw/node` for API mocking in tests
- `@testing-library/user-event` for interaction testing
- Wrap components in `QueryClientProvider` + `MemoryRouter` test utilities
- Test SignalR hook with MSW or manual mock of `HubConnection`
