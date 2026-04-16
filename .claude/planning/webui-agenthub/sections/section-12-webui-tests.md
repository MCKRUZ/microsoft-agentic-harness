# Section 12: Presentation.WebUI — Testing

## Implementation Status: COMPLETE

**Commit:** feat: implement shared MSW test infrastructure (section 12)
**Tests:** 55 total (19 files) — all passing
**Deviations from plan:**
- All chat and telemetry feature tests were already written in sections 10/11 (inline MSW servers). Section 12 consolidated them under the shared server and removed the inline `setupServer` instances from `ToolsBrowser.test.tsx` and `McpLists.test.tsx`.
- `scaffold.test.ts` deleted (redundant once `infrastructure.test.ts` existed).
- SignalR mock in `infrastructure.test.ts` uses a class-based `MockHubConnectionBuilder` (not `vi.fn().mockReturnValue`) — Vitest rejects `mockReturnValue` with `new`.
- Prompts handler fixture restored to `arguments: [{ name: 'text' }]` after review caught branch coverage drop; `McpLists.test.tsx` adds assertion for `'Args: text'`.

**Files created:**
- `src/test/handlers.ts` — shared MSW `setupServer` with JSDoc contract documentation
- `src/test/infrastructure.test.ts` — 3 validation tests (renderWithProviders, MSW fixtures, SignalR mock)

**Files modified:**
- `src/test/setup.ts` — wired shared MSW server lifecycle (beforeAll/afterEach/afterAll)
- `src/features/telemetry/__tests__/ToolsBrowser.test.tsx` — removed inline server, uses `server` from `@/test/handlers`
- `src/features/telemetry/__tests__/McpLists.test.tsx` — removed inline server, uses shared handlers; added `Args: text` assertion
- `vitest.config.ts` — added coverage `exclude` array

**Files deleted:**
- `src/test/scaffold.test.ts` — redundant

---

## Overview

This section sets up the complete test infrastructure for the WebUI project and writes feature-level unit/integration tests for the chat and telemetry features. It depends on sections 09 (MSAL Auth), 10 (Chat Feature), and 11 (Telemetry/MCP Panel) being complete. Target: ≥ 80% coverage.

**Verify command:** `cd src/Content/Presentation/Presentation.WebUI && npm test`

**Dependencies:**
- Section 09: `useAuth`, `apiClient`, `buildHubConnection`, `useAgentHub`
- Section 10: `useChatStore`, `ChatInput`, `MessageList`, `TypingIndicator`
- Section 11: `buildSpanTree`, `useTelemetryStore`, `SpanNode`, `TracesPanel`, `ToolsBrowser`, `ToolInvoker`, `ResourcesList`, `PromptsList`

---

## Files to Create

```
src/Content/Presentation/Presentation.WebUI/src/test/
  setup.ts
  utils.tsx
  handlers.ts
features/
  chat/
    __tests__/
      useChatStore.test.ts
      ChatInput.test.tsx
      MessageList.test.tsx
      TypingIndicator.test.tsx
  telemetry/
    __tests__/
      buildSpanTree.test.ts
      useTelemetryStore.test.ts
      SpanNode.test.tsx
      TracesPanel.test.tsx
  mcp/
    __tests__/
      ToolsBrowser.test.tsx
      ToolInvoker.test.tsx
      ResourcesList.test.tsx
      PromptsList.test.tsx
```

---

## Test Infrastructure (Write These First)

### `src/test/setup.ts`

Imports `@testing-library/jest-dom`, wires MSW server lifecycle:

```ts
import '@testing-library/jest-dom';
import { server } from './handlers';

beforeAll(() => server.listen({ onUnhandledRequest: 'error' }));
afterEach(() => server.resetHandlers());
afterAll(() => server.close());
```

Referenced in `vitest.config.ts` via `setupFiles: ['src/test/setup.ts']`.

### `src/test/handlers.ts`

Creates and exports an MSW `setupServer(...)` instance plus the handler array so individual tests can override handlers.

Handlers to implement (using MSW v2 `http` API):

| Route | Response |
|---|---|
| `GET /api/agents` | `[{ name: 'research-agent', description: 'A research agent' }]` |
| `GET /api/mcp/tools` | Array with at least one tool: `{ name, description, inputSchema }` |
| `POST /api/mcp/tools/:name/invoke` | `{ success: true, output: 'tool result' }` |
| `GET /api/mcp/resources` | Array with one resource: `{ uri, name, description }` |
| `GET /api/mcp/prompts` | Array with one prompt: `{ name, description, arguments: [] }` |

Stub signature:

```ts
// src/test/handlers.ts
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';

export const handlers = [ /* http.get(...), http.post(...) */ ];
export const server = setupServer(...handlers);
```

### `src/test/utils.tsx`

Exports `renderWithProviders(ui, options?)`. Wraps the component under test in:
- A fresh `QueryClient` (retries: 0, gcTime: 0)
- `MemoryRouter`
- A mock `MsalProvider` (use `PublicClientApplication` with a no-op config or a simple context mock that satisfies the `useMsal` hook)
- `ThemeProvider`

```tsx
// src/test/utils.tsx
import { render, RenderOptions } from '@testing-library/react';
import { ReactElement } from 'react';

export function renderWithProviders(
  ui: ReactElement,
  options?: Omit<RenderOptions, 'wrapper'>
): ReturnType<typeof render>;
```

The MSAL mock only needs to satisfy `useIsAuthenticated()` returning `true` and `useMsal()` returning an `instance` with stub `acquireTokenSilent` and `acquireTokenPopup` methods.

---

## Infrastructure Validation Tests (Section 12 Self-Tests)

These confirm the test infrastructure itself works. Place in `src/test/infrastructure.test.ts`.

- `renderWithProviders renders children without crashing` — render a `<div>test</div>`, assert it's in the document
- `MSW handlers return expected fixtures for all /api/* routes` — use `fetch` (or `axios`) inside a test to call each handler, assert shape
- `HubConnection mock captures registered event handlers` — verify the `vi.mock('@microsoft/signalr')` pattern captures `on(event, handler)` calls

---

## SignalR Mock Pattern

For any test that uses `useAgentHub`, mock the module at the top of the test file:

```ts
vi.mock('@microsoft/signalr', () => {
  const handlers: Record<string, (...args: unknown[]) => void> = {};
  const mockConnection = {
    start: vi.fn().mockResolvedValue(undefined),
    stop: vi.fn().mockResolvedValue(undefined),
    invoke: vi.fn().mockResolvedValue(undefined),
    on: vi.fn((event: string, handler: (...args: unknown[]) => void) => {
      handlers[event] = handler;
    }),
    off: vi.fn(),
    onclose: vi.fn(),
    state: 'Connected',
    _handlers: handlers, // expose for test assertions
  };
  return {
    HubConnectionBuilder: vi.fn().mockReturnValue({
      withUrl: vi.fn().mockReturnThis(),
      withAutomaticReconnect: vi.fn().mockReturnThis(),
      configureLogging: vi.fn().mockReturnThis(),
      build: vi.fn().mockReturnValue(mockConnection),
    }),
    LogLevel: { Information: 1 },
    HubConnectionState: { Connected: 'Connected', Disconnected: 'Disconnected' },
  };
});
```

To simulate incoming events in tests, call `mockConnection._handlers['EventName'](payload)` directly.

---

## Chat Feature Tests

### `useChatStore.test.ts`

Tests for the Zustand store in isolation (no rendering needed).

- `appendToken accumulates tokens in streamingContent` — call `appendToken('hello ')`, `appendToken('world')`, assert `streamingContent === 'hello world'`
- `finalizeStream clears streamingContent and adds message to messages array` — after `appendToken`, call `finalizeStream()`, assert `streamingContent === ''` and `messages` has one assistant message
- `clearMessages resets all state` — after populating, call `clearMessages()`, assert empty state

### `ChatInput.test.tsx`

Uses `renderWithProviders`. Pass a `sendMessage` spy as a prop.

- `ChatInput submit calls sendMessage with input value`
- `ChatInput is disabled while isStreaming is true` — set store `isStreaming: true`, assert textarea and button are disabled
- `ChatInput clears after submit` — assert input value is empty after form submission
- `ChatInput rejects empty string (does not call sendMessage)` — submit with empty field, assert `sendMessage` not called
- `ChatInput rejects messages over 4000 characters` — fill 4001 chars, assert validation error shown and `sendMessage` not called

### `MessageList.test.tsx`

Uses `renderWithProviders`, pre-populates `useChatStore` with test messages.

- `MessageList renders all messages from the store`
- `MessageList renders user message right-aligned` — assert CSS class or text-align style
- `MessageList renders assistant message left-aligned`
- `Streaming: TokenReceived event updates visible text in DOM` — simulate `appendToken(...)` calls, assert new text appears
- `TurnComplete event finalizes assistant message and hides TypingIndicator` — simulate `finalizeStream()`, assert typing indicator gone

### `TypingIndicator.test.tsx`

- `TypingIndicator is visible when isStreaming is true`
- `TypingIndicator is not rendered when isStreaming is false`

---

## Telemetry Feature Tests

### `buildSpanTree.test.ts`

Pure function — no rendering, no providers needed. Import `buildSpanTree` directly.

- `buildSpanTree returns empty array for empty input`
- `buildSpanTree nests child spans under their parent by parentSpanId` — provide parent span (id: `"a"`, parentSpanId: null) and child span (id: `"b"`, parentSpanId: `"a"`); assert result has one root with one child
- `buildSpanTree handles root spans with null parentSpanId` — span with parentSpanId null should be a root
- `buildSpanTree handles multiple disjoint trace trees` — two root spans with separate children; assert two roots in result
- `buildSpanTree result is stable for same input` — call twice with same array reference, assert same output object reference (validates memoization)

### `useTelemetryStore.test.ts`

- `addGlobalSpan caps at MAX_GLOBAL_SPANS, dropping oldest` — add 501 spans, assert store has exactly 500 and oldest is gone
- `clearAll resets both conversationSpans and globalSpans`

### `SpanNode.test.tsx`

- `SpanNode renders green indicator for ok status`
- `SpanNode renders red indicator for error status`
- `SpanNode renders grey indicator for unset status`
- `Clicking SpanNode expands SpanDetail with tags` — use `userEvent.click`, assert tags become visible

### `TracesPanel.test.tsx`

- `TracesPanel with empty spans renders empty state placeholder`
- `TracesPanel renders correct number of root SpanTree components` — provide two disjoint trees, assert two `SpanTree` roots rendered

---

## MCP Feature Tests

### `ToolsBrowser.test.tsx`

Uses `renderWithProviders`. MSW handler serves the tool list.

- `ToolsBrowser renders tool names from MSW mock` — wait for tools to load (`findByText`), assert tool name is in the DOM
- `Clicking a tool shows its description and schema` — click a tool item, assert description and JSON schema appear

### `ToolInvoker.test.tsx`

- `ToolInvoker Direct mode submit calls useInvokeTool mutation` — spy on the `POST /api/mcp/tools/:name/invoke` MSW handler, submit the form, assert it was called
- `ToolInvoker Via Agent mode submit calls invokeToolViaAgent` — toggle to "Via Agent", submit, assert SignalR `invoke` was called with the right method name
- `ToolInvoker shows response after successful invocation` — assert response text rendered after MSW returns success
- `ToolInvoker shows error after failed invocation` — override MSW handler to return 400, assert error message shown

### `ResourcesList.test.tsx`

- `ResourcesList renders resource URI, name, and description`

### `PromptsList.test.tsx`

- `PromptsList renders prompt name and description`

---

## Vitest Configuration

Ensure `vitest.config.ts` (or `vite.config.ts` test block) includes:

```ts
test: {
  environment: 'jsdom',
  setupFiles: ['src/test/setup.ts'],
  globals: true,
  coverage: {
    provider: 'v8',
    thresholds: { lines: 80, functions: 80, branches: 80, statements: 80 },
    exclude: ['src/test/**', 'src/components/ui/**', '**/*.d.ts'],
  },
}
```

The `src/components/ui/**` exclusion covers shadcn/ui primitives (copied third-party code, not authored logic).

---

## Coverage Target

80% on lines, functions, branches, and statements across:
- `src/features/**`
- `src/stores/**`
- `src/hooks/**`
- `src/lib/**`

Exclude `src/test/**`, `src/components/ui/**`, and `src/types/**` from coverage measurement.

---

## Dependency Notes

- MSW v2 API uses `http.get(...)` not `rest.get(...)` — confirm the version installed in section 01 before writing handlers
- `@testing-library/user-event` v14+ is async by default — always `await userEvent.click(...)`, `await userEvent.type(...)`
- The MSAL mock must satisfy `@azure/msal-react`'s context shape; if `PublicClientApplication` constructor is too heavy for tests, use a manual context mock injected via `MsalContext.Provider`
- `react-window`'s `VariableSizeList` requires a DOM with a measured height — set `element.style.height = '600px'` in tests or mock `react-window` with a simple passthrough renderer if virtualization causes test failures
