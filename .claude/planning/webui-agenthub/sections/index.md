<!-- PROJECT_CONFIG
runtime: typescript-npm
test_command: dotnet test src/AgenticHarness.slnx
END_PROJECT_CONFIG -->

<!-- SECTION_MANIFEST
section-01-scaffolding
section-02-agenthub-core
section-03-conversation-store
section-04-signalr-hub
section-05-otel-bridge
section-06-mcp-api
section-07-agenthub-tests
section-08-webui-shell
section-09-msal-auth
section-10-chat-feature
section-11-telemetry-mcp-panel
section-12-webui-tests
section-13-integration
END_MANIFEST -->

# Implementation Sections Index

Two-project implementation: `Presentation.AgentHub` (.NET 10 ASP.NET Core) + `Presentation.WebUI` (Vite + React 19 + TypeScript).

**Note on test commands:** AgentHub sections (01–07) verify with `dotnet test src/AgenticHarness.slnx`. WebUI sections (08–12) verify with `cd src/Content/Presentation/Presentation.WebUI && npm test`. Section 13 runs both.

---

## Dependency Graph

| Section | Depends On | Can Parallelize With |
|---------|------------|---------------------|
| section-01-scaffolding | — | — |
| section-02-agenthub-core | 01 | 08 |
| section-03-conversation-store | 02 | 05, 06, 09 |
| section-04-signalr-hub | 02, 03 | 05, 06, 10, 11 |
| section-05-otel-bridge | 02 | 03, 06, 09 |
| section-06-mcp-api | 02 | 03, 05, 09 |
| section-07-agenthub-tests | 02, 03, 04, 05, 06 | 12 |
| section-08-webui-shell | 01 | 02 |
| section-09-msal-auth | 08 | 03, 05, 06 |
| section-10-chat-feature | 09 | 04, 11 |
| section-11-telemetry-mcp-panel | 09 | 04, 10 |
| section-12-webui-tests | 09, 10, 11 | 07 |
| section-13-integration | 07, 12 | — |

---

## Execution Order (Parallel Batches)

**Batch 1:** section-01-scaffolding

**Batch 2:** section-02-agenthub-core, section-08-webui-shell *(parallel — different projects)*

**Batch 3:** section-03-conversation-store, section-05-otel-bridge, section-06-mcp-api, section-09-msal-auth *(parallel — 03/05/06 depend on 02; 09 depends on 08)*

**Batch 4:** section-04-signalr-hub, section-10-chat-feature, section-11-telemetry-mcp-panel *(parallel — 04 depends on 02+03; 10/11 depend on 09)*

**Batch 5:** section-07-agenthub-tests, section-12-webui-tests *(parallel — 07 depends on 02–06; 12 depends on 09–11)*

**Batch 6:** section-13-integration *(depends on 07 + 12)*

---

## Section Summaries

### section-01-scaffolding
Create both project directories, `.csproj` for AgentHub + test project, Vite scaffold for WebUI, install all npm deps, add .csproj files to `AgenticHarness.slnx`. Verify `dotnet build` and `npm run build` both succeed.

### section-02-agenthub-core
`Program.cs`, `DependencyInjection.cs`, `appsettings.json` for AgentHub. Wires `Presentation.Common.GetServices(true)`, Azure AD auth with explicit SignalR `OnMessageReceived`, CORS (no credentials), rate limiting, SignalR with `CloseOnAuthenticationExpiration`, and `AgentHubConfig` binding. Middleware order: UseRouting → UseCors → UseAuthentication → UseAuthorization → UseRateLimiter → Map*. Full XML docs on all public types.

### section-03-conversation-store
`IConversationStore` interface, `ConversationMessage`/`ConversationRecord` presentation models, `FileSystemConversationStore` with atomic writes (tmp→move), path traversal protection, global semaphore for thread safety, `GetHistoryForDispatch(maxMessages)`. `AgentsController` with ownership enforcement (403 on cross-user access).

### section-04-signalr-hub
`AgentTelemetryHub` with all client-to-server methods. Ownership validation on every conversation-scoped method. Per-conversation turn serialization (`ConcurrentDictionary<string, SemaphoreSlim>`). Simulated streaming (50-char chunks). Error handling that sanitizes messages and appends synthetic error to conversation record. `JoinGlobalTraces` gated on `AgentHub.Traces.ReadAll` role.

### section-05-otel-bridge
`SignalRSpanExporter` as `BaseExporter<Activity>` + `IHostedService`. `Channel<SpanData>` (bounded 1000, DropOldest). `MapToSpanData` extracts `agent.conversation_id` tag into `ConversationId`, sets `ParentSpanId` to null for root spans. Drain loop uses `await Task.WhenAll(...)` — no Task.Run. Routes by `ConversationId` to conversation group AND always to global-traces group. Registration in OTel pipeline after `GetServices()`.

### section-06-mcp-api
`McpController` with `GET /api/mcp/tools`, `GET /api/mcp/resources`, `GET /api/mcp/prompts`, `POST /api/mcp/tools/{name}/invoke`. Audit logging (structured log, input hash, no raw args at Info). 32KB request size limit. Sanitized error responses. Returns empty array (not 500) when provider not registered.

### section-07-agenthub-tests
`TestWebApplicationFactory`, `TestAuthHandler`. Integration tests for hub auth, IDOR ownership, turn serialization, global traces role gate, conversation store file operations, MCP controller endpoints, rate limiting. Full XML docs.

### section-08-webui-shell
Vite Tailwind + shadcn/ui init. Folder structure scaffold. `App.tsx` with `MsalProvider`/`QueryClientProvider`/`ThemeProvider`/Router composition. `AppShell`, `SplitPanel` (CSS Grid, resizable), `Header` (placeholder agent selector, theme toggle). `ThemeProvider` with localStorage persistence. All shadcn/ui components copied into `src/components/ui/`.

### section-09-msal-auth
`authConfig.ts` (two-app model: SPA clientId + API scope `api://{apiClientId}/access_as_user`). `useAuth` hook (acquireTokenSilent + popup fallback). `apiClient` axios instance with MSAL token interceptor and 401 redirect. `buildHubConnection` factory with `accessTokenFactory`. `useAgentHub` hook (useRef connection, connection state machine, all event handlers dispatching to Zustand stores, React 19 StrictMode-safe cleanup).

### section-10-chat-feature
`useChatStore` (Zustand: messages, isStreaming, streamingContent). `ChatPanel`, `MessageList` (react-window VariableSizeList), `MessageItem` (role-based rendering, code block regex), `TypingIndicator`, `ChatInput` (RHF + Zod, Enter/Shift+Enter, disabled while streaming). Streaming token rendering (append → finalize on TurnComplete).

### section-11-telemetry-mcp-panel
`useTelemetryStore` (conversationSpans, globalSpans capped at 500, clearAll). `RightPanel` with 5 tabs. `TracesPanel` → `buildSpanTree` (pure, memoized with useMemo) → `SpanTree`/`SpanNode` (duration bars, status colors, collapsible) → `SpanDetail`. Agent selector (`useAgentsQuery` TanStack Query). `ToolsBrowser` + `ToolInvoker` (Direct/Via Agent toggle). `ResourcesList`, `PromptsList`.

### section-12-webui-tests
`src/test/setup.ts` (MSW server, jest-dom). `src/test/utils.tsx` (renderWithProviders with mock MSAL + QueryClient + Router). MSW handlers for all /api/* routes. `vi.mock('@microsoft/signalr')` pattern. Tests for useChatStore, ChatInput, MessageList streaming, buildSpanTree, SpanNode, ToolsBrowser invocation. Coverage ≥ 80%.

### section-13-integration
`.env.example`, `docs/azure-ad-setup.md` (two-app registration steps). `dev:all` concurrently script. README section for both projects. Final verification: dotnet build ✓, dotnet test ✓, npm run build ✓, npm test ✓.
