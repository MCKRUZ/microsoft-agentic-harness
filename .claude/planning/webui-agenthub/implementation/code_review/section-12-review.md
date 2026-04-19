# Section 12 - Shared MSW Test Infrastructure: Code Review

**Reviewer:** claude-code-reviewer
**Date:** 2026-04-16
**Scope:** Shared MSW server instance (handlers.ts), global test lifecycle (setup.ts), infrastructure validation tests (infrastructure.test.ts), vitest coverage config, migration of ToolsBrowser.test.tsx and McpLists.test.tsx to shared server

---

## Summary

Clean consolidation of per-file MSW server instances into a shared server with centralized lifecycle. The migration correctly removes duplicated setupServer/beforeAll/afterEach/afterAll from two test files and wires the shared server in setup.ts. The infrastructure validation tests are meaningful and well-structured. One real correctness issue with fixture data, one design concern worth noting.

**Verdict: APPROVE -- no CRITICAL or HIGH issues. Two MEDIUM items worth addressing. Clean section overall.**

---

## CRITICAL

None.

---

## HIGH

None.

---

## MEDIUM

### M1. Prompts fixture data silently changed -- arguments array emptied

**File:** src/test/handlers.ts:28 vs old McpLists.test.tsx inline handler

The old inline handler returned `arguments: [{ name: 'text' }]` for the summarize prompt. The shared handler returns `arguments: []`. The McpLists test passes because it only asserts on `name` and `description`, never on the arguments rendering. However:

1. The PromptsList component (PromptsList.tsx:24-27) conditionally renders an "Args:" line when `arguments.length > 0`. The old fixture would have rendered "Args: text"; the new fixture renders nothing. There is no test covering the arguments rendering path.
2. Any future test asserting `screen.getByText('Args: text')` would fail unexpectedly because the shared fixture has no arguments.

**Fix:** Restore the original fixture shape with `arguments: [{ name: 'text' }]` and add a test assertion in McpLists.test.tsx for the arguments line. This covers a currently-untested rendering branch.

---

### M2. onUnhandledRequest: 'error' requires all test files to either use handlers or avoid HTTP

**File:** src/test/setup.ts:24

The global `onUnhandledRequest: 'error'` policy means ANY unhandled HTTP request from ANY test file will throw. This is the right strictness level, but it creates a contract: every test that triggers a real HTTP call must either (a) be covered by the shared handlers, or (b) add a `server.use()` override.

Reviewed all 19 test files in the suite:
- `apiClient.test.ts` uses a custom axios adapter (no actual HTTP) -- safe.
- `useAgentHub.test.ts` mocks signalrClient entirely -- safe.
- `ChatInput.test.tsx`, `MessageList.test.tsx`, `useChatStore.test.ts`, `TypingIndicator.test.tsx` are pure component/store tests with no HTTP -- safe.
- `App.test.tsx` mocks the agentHub hook and MSAL, uses `render` not `renderWithProviders` so no QueryClient fetches -- safe.
- `Header.test.tsx`, `SplitPanel.test.tsx`, `ThemeProvider.test.tsx` are UI-only -- safe.
- `buildSpanTree.test.ts`, `useTelemetryStore.test.ts`, `SpanNode.test.tsx`, `TracesPanel.test.tsx` are telemetry unit tests -- safe.
- `scaffold.test.ts` -- trivial assertion, safe.

**Conclusion:** No existing test files will break under the 'error' policy. But this should be documented in handlers.ts with a comment explaining the contract for future contributors.

**Fix:** Add a JSDoc comment at the top of handlers.ts explaining:
```typescript
/**
 * Shared MSW handlers for all test files. The global setup uses
 * onUnhandledRequest: 'error', so any test making HTTP calls to a
 * route not listed here must add a server.use() override.
 */
```

---

## LOW

### L1. infrastructure.test.ts imports `render` from Testing Library but doesn't use it directly

**File:** src/test/infrastructure.test.ts:2

`render` is imported but only `renderWithProviders` (dynamically imported) is used for actual rendering. The `screen` import IS used. Unused import is harmless but noisy.

**Fix:** Remove `render` from the import statement on line 2.

---

### L2. infrastructure.test.ts imports `axios` directly -- coupling to HTTP client

**File:** src/test/infrastructure.test.ts:3, 58

The MSW handler validation test uses `axios.get()` to verify fixtures. This couples the infrastructure test to axios as the HTTP client. If the project ever switches to fetch or a different client, this test needs updating.

This is a minor concern since the test is explicitly testing MSW interception (which is transport-agnostic). Using `fetch()` would be more universal, but axios works fine here since the project already depends on it.

**Suggestion:** No action needed now. Just noting the coupling.

---

### L3. ToolsBrowser.test.tsx still imports http/HttpResponse from msw and server from handlers

**File:** src/features/telemetry/__tests__/ToolsBrowser.test.tsx:8-9

The file correctly imports `server` from `@/test/handlers` for `server.use()` overrides (line 81-85). It also imports `http` and `HttpResponse` from `msw` for constructing the override. These are all necessary -- no issue here, just confirming the pattern is correct.

---

### L4. scaffold.test.ts is now redundant

**File:** src/test/scaffold.test.ts

With infrastructure.test.ts providing real validation of the test infrastructure (renderWithProviders, MSW handlers, SignalR mock), the scaffold.test.ts file (`expect(true).toBe(true)`) has no remaining value.

**Suggestion:** Delete scaffold.test.ts.

---

## INFO

### I1. server.use() override pattern works correctly with shared server

The ToolsBrowser.test.tsx error test (line 81-85) correctly uses `server.use()` to override the POST handler for a single test. Because setup.ts calls `server.resetHandlers()` in `afterEach`, this override is automatically cleaned up. The pattern is sound.

---

### I2. vi.clearAllMocks() in ToolsBrowser beforeEach is a safe replacement

The old code used `afterEach(() => { server.resetHandlers(); mockInvokeToolViaAgent.mockClear(); })`. The new code uses `beforeEach(() => { vi.clearAllMocks(); ... })`. This is behaviorally equivalent -- `vi.clearAllMocks()` is broader (clears all mocks, not just one), and `beforeEach` is arguably better placement since it guarantees clean state before each test regardless of prior test failures. The `server.resetHandlers()` is now handled globally in setup.ts.

---

### I3. Coverage exclude array is well-chosen

**File:** vitest.config.ts:20

Excluding `src/test/**`, `src/components/ui/**` (shadcn generated), `**/*.d.ts`, and `src/types/**` from coverage is correct. Test infrastructure and auto-generated UI primitives should not count toward coverage thresholds.

---

### I4. SignalR mock in infrastructure.test.ts is a useful reference pattern

The class-based SignalR mock (lines 10-37) demonstrates the correct pattern for mocking `@microsoft/signalr` in a way that supports `new HubConnectionBuilder().withUrl().withAutomaticReconnect().build()` chaining. This is a good reference for future test authors. However, it is local to this test file -- if other tests need the same mock, it should be extracted to a shared module (similar to how handlers.ts was extracted).

---

## Findings Summary

| Severity | Count |
|----------|-------|
| CRITICAL | 0     |
| HIGH     | 0     |
| MEDIUM   | 2     |
| LOW      | 4     |
| INFO     | 4     |
| **Total**| **10**|

---

## Verdict

**APPROVE** -- No blocking issues. M1 (restore prompts fixture arguments) is worth fixing to avoid a silently untested rendering branch. M2 (document the onUnhandledRequest contract) is a one-line comment addition. Both are quick fixes. The consolidation pattern is clean and the infrastructure tests add real value.
