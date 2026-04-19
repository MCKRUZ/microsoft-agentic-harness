# TDD Plan: Presentation.AgentHub + Presentation.WebUI

Mirrors `claude-plan.md` section structure. For each section, defines tests to write BEFORE implementing.

Testing frameworks: xUnit + WebApplicationFactory (AgentHub), Vitest + RTL + MSW (WebUI).

---

## Section 1: Solution Structure and Project Scaffolding

Write these tests first to validate the scaffold compiles and is wired correctly:

### AgentHub
- Test: `AgentHub project builds without errors` — `dotnet build` exits 0
- Test: `AgentHub test project discovers at least one test` — `dotnet test` finds the test assembly
- Test: `Presentation.Common.GetServices registers IMediator` — simple DI resolution smoke test

### WebUI
- Test: `npm run build succeeds` — TypeScript compiles, Vite bundles without error
- Test: `vitest finds test files` — at least one test file discovered

---

## Section 2: Presentation.AgentHub — Core Setup

### Middleware and Auth
- Test: `GET /api/agents without token returns 401`
- Test: `POST /hubs/agent WebSocket upgrade without token is rejected`
- Test: `GET /api/agents with valid test token returns 200` (uses TestAuthHandler)
- Test: `OPTIONS /api/agents with CORS preflight from localhost:5173 returns allowed headers` (verify CORS policy wired)
- Test: `UseCors is invoked before UseAuthentication` — integration: preflight does not return 401

### Rate limiting
- Test: `POST /api/mcp/tools/{name}/invoke called 11 times in quick succession returns 429 on the 11th call`

---

## Section 3: Presentation.AgentHub — Conversation Store and Agent Execution

### FileSystemConversationStore
- Test: `CreateAsync writes a JSON file at the expected path`
- Test: `GetAsync returns null for unknown conversationId`
- Test: `GetAsync deserializes the conversation record correctly`
- Test: `AppendMessageAsync updates the file atomically` — write to tmp then move
- Test: `DeleteAsync removes the file`
- Test: `ListAsync returns only conversations belonging to the given userId`
- Test: `Concurrent AppendMessageAsync calls on the same conversationId do not corrupt the file`
- Test: `Path with traversal characters in conversationId throws ArgumentException`
- Test: `GetHistoryForDispatch returns at most MaxHistoryMessages entries`
- Test: `GetHistoryForDispatch returns the LAST N messages, not the first N`

### AgentsController
- Test: `GET /api/conversations returns only conversations owned by the authenticated user`
- Test: `GET /api/conversations/{id} for another user's conversation returns 403`
- Test: `DELETE /api/conversations/{id} for another user's conversation returns 403`
- Test: `DELETE /api/conversations/{id} for own conversation returns 204 and removes the file`

---

## Section 4: Presentation.AgentHub — SignalR Hub

### Authentication and authorization
- Test: `Unauthenticated SignalR connection is rejected`
- Test: `StartConversation with another user's conversationId throws HubException`
- Test: `SendMessage on another user's conversationId throws HubException`
- Test: `JoinConversationGroup on another user's conversationId throws HubException`
- Test: `JoinGlobalTraces without AgentHub.Traces.ReadAll role throws HubException`
- Test: `JoinGlobalTraces with AgentHub.Traces.ReadAll role succeeds`

### Chat flow
- Test: `StartConversation creates a new ConversationRecord in the store`
- Test: `StartConversation on existing conversation returns last 20 messages`
- Test: `SendMessage dispatches ExecuteAgentTurnCommand via IMediator`
- Test: `SendMessage emits TokenReceived events before TurnComplete`
- Test: `SendMessage on mediator exception emits Error event with sanitized message`
- Test: `SendMessage on mediator exception appends synthetic error message to conversation store`
- Test: `Two rapid SendMessage calls on same conversation complete in order (no interleaved events)`

---

## Section 5: Presentation.AgentHub — OTel → SignalR Bridge

### Channel and export
- Test: `Export with full channel (capacity exceeded) does not block` — verify TryWrite returns within 1ms
- Test: `Export logs a warning when channel is full and a span is dropped`
- Test: `MapToSpanData sets ParentSpanId to null for root spans`
- Test: `MapToSpanData extracts agent.conversation_id tag into ConversationId field`
- Test: `MapToSpanData sets ConversationId to null when tag is absent`

### Drain loop
- Test: `Span with ConversationId is sent to conversation:{conversationId} group`
- Test: `Span is always sent to global-traces group regardless of ConversationId`
- Test: `StopAsync completes the channel and drain loop exits cleanly`

---

## Section 6: Presentation.AgentHub — MCP Artifacts API

- Test: `GET /api/mcp/tools returns 200 with tool list`
- Test: `GET /api/mcp/tools returns tool name, description, and schema`
- Test: `GET /api/mcp/prompts returns empty array when no prompt provider registered`
- Test: `POST /api/mcp/tools/{name}/invoke with valid args returns 200 with output`
- Test: `POST /api/mcp/tools/nonexistent/invoke returns 404`
- Test: `POST /api/mcp/tools/{name}/invoke with tool execution error returns 200 with Success=false`
- Test: `POST /api/mcp/tools/{name}/invoke emits structured audit log entry`
- Test: `POST /api/mcp/tools/{name}/invoke body exceeding 32KB returns 413`
- Test: `POST /api/mcp/tools/{name}/invoke audit log does not include raw arguments at Information level`

---

## Section 7: Presentation.AgentHub — Tests

This section describes the test infrastructure itself. No prior tests needed — it IS the tests. Verify after implementation:
- Test: `TestWebApplicationFactory starts without errors`
- Test: `TestAuthHandler returns authenticated ClaimsPrincipal for any request`
- Test: `TestWebApplicationFactory uses temp directory for FileSystemConversationStore`

---

## Section 8: Presentation.WebUI — Project Setup and App Shell

- Test: `App renders without crashing when MSAL is in authenticated state (mock)`
- Test: `App renders login redirect when MSAL is not authenticated`
- Test: `SplitPanel renders left and right children`
- Test: `SplitPanel left panel is accessible (has landmark role or aria-label)`
- Test: `Header renders app name`
- Test: `ThemeProvider applies data-theme="dark" to html element when dark mode selected`
- Test: `ThemeProvider persists theme selection to localStorage`

---

## Section 9: Presentation.WebUI — MSAL Auth and API Client

- Test: `useAuth.acquireToken returns token from acquireTokenSilent`
- Test: `useAuth.acquireToken falls back to acquireTokenPopup on InteractionRequiredAuthError`
- Test: `apiClient attaches Authorization Bearer header to requests`
- Test: `apiClient redirects to login on 401 response`
- Test: `buildHubConnection creates connection with accessTokenFactory`
- Test: `useAgentHub starts in disconnected state`
- Test: `useAgentHub transitions to connected state after start()`
- Test: `useAgentHub cleanup calls connection.stop() on unmount`

---

## Section 10: Presentation.WebUI — Chat Feature

### useChatStore
- Test: `appendToken accumulates tokens in streamingContent`
- Test: `finalizeStream clears streamingContent and adds message to messages array`
- Test: `clearMessages resets all state`

### ChatInput
- Test: `ChatInput submit calls sendMessage with input value`
- Test: `ChatInput is disabled while isStreaming is true`
- Test: `ChatInput clears after submit`
- Test: `ChatInput rejects empty string (does not call sendMessage)`
- Test: `ChatInput rejects messages over 4000 characters`

### MessageList
- Test: `MessageList renders all messages from the store`
- Test: `MessageList renders user message right-aligned`
- Test: `MessageList renders assistant message left-aligned`
- Test: `Streaming: TokenReceived event updates visible text in DOM`
- Test: `TurnComplete event finalizes assistant message and hides TypingIndicator`

### TypingIndicator
- Test: `TypingIndicator is visible when isStreaming is true`
- Test: `TypingIndicator is not rendered when isStreaming is false`

---

## Section 11: Presentation.WebUI — Telemetry and MCP Panel

### buildSpanTree (pure function — unit tests only, no rendering)
- Test: `buildSpanTree returns empty array for empty input`
- Test: `buildSpanTree nests child spans under their parent by parentSpanId`
- Test: `buildSpanTree handles root spans with null parentSpanId`
- Test: `buildSpanTree handles multiple disjoint trace trees`
- Test: `buildSpanTree result is stable for same input (referential check for memoization)`

### Telemetry store
- Test: `addGlobalSpan caps at MAX_GLOBAL_SPANS, dropping oldest`
- Test: `clearAll resets both conversationSpans and globalSpans`

### SpanNode
- Test: `SpanNode renders green indicator for ok status`
- Test: `SpanNode renders red indicator for error status`
- Test: `SpanNode renders grey indicator for unset status`
- Test: `Clicking SpanNode expands SpanDetail with tags`

### TracesPanel
- Test: `TracesPanel with empty spans renders empty state placeholder`
- Test: `TracesPanel renders correct number of root SpanTree components`

### ToolsBrowser
- Test: `ToolsBrowser renders tool names from MSW mock`
- Test: `Clicking a tool shows its description and schema`
- Test: `ToolInvoker Direct mode submit calls useInvokeTool mutation`
- Test: `ToolInvoker Via Agent mode submit calls invokeToolViaAgent`
- Test: `ToolInvoker shows response after successful invocation`
- Test: `ToolInvoker shows error after failed invocation`

### ResourcesList / PromptsList
- Test: `ResourcesList renders resource URI, name, and description`
- Test: `PromptsList renders prompt name and description`

---

## Section 12: Presentation.WebUI — Testing

This section is the test infrastructure itself. Verify after implementation:
- Test: `renderWithProviders renders children without crashing`
- Test: `MSW handlers return expected fixtures for all /api/* routes`
- Test: `HubConnection mock captures registered event handlers`

---

## Section 13: Integration and Developer Workflow

- Test: `npm run build exits 0` (CI-runnable)
- Test: `dotnet build src/AgenticHarness.slnx exits 0`
- Test: `dotnet test src/AgenticHarness.slnx exits 0`
- Test: `npm run test:coverage produces coverage ≥ 80%`
- Test: `Vite proxy config forwards /api/* — verified by build output or vite.config.ts unit test`
