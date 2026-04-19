# Section 09: Presentation.WebUI — MSAL Auth and API Client

## Overview

This section implements the authentication plumbing for the WebUI: the `useAuth` hook, the `apiClient` axios instance with token interceptor, the `buildHubConnection` SignalR factory, and the `useAgentHub` hook that wires real-time events to Zustand stores. After this section, authenticated REST calls and SignalR connections work from the browser.

**Depends on:** section-08-webui-shell (project scaffold, `MsalProvider` wiring, folder structure, `authConfig.ts` skeleton)

**Parallel with:** section-03-conversation-store, section-05-otel-bridge, section-06-mcp-api

**Verify with:** `cd src/Content/Presentation/Presentation.WebUI && npm test`

---

## Two-App Azure AD Model (Background)

This is the correct architecture for a SPA calling a protected API. Two app registrations are required:

- **API app (`AgentHub`)**: registered in Azure AD, exposes the `access_as_user` scope. Application ID URI is `api://{apiClientId}`. `appsettings.json` `AzureAd:ClientId` refers to this registration.
- **SPA app (`AgentWebUI`)**: registered as a Single Page Application with redirect URI `http://localhost:5173`. Requests the delegated scope `api://{apiClientId}/access_as_user`. `VITE_AZURE_CLIENT_ID` refers to this registration.

The scope in `authConfig.ts` is constructed as `api://${VITE_AZURE_API_CLIENT_ID}/access_as_user`.

---

## Tests First

These tests live in `src/Content/Presentation/Presentation.WebUI/src/test/` (or co-located with the module under `__tests__`). Write them before implementing.

The test utilities from section-12 (`renderWithProviders`, MSW handlers, SignalR mock) are not yet available — stub only what each test needs via inline mocks. Section-12 will replace the inline stubs with the shared utilities.

### `useAuth` tests

```ts
// src/hooks/__tests__/useAuth.test.ts

describe('useAuth', () => {
  it('acquireToken returns token from acquireTokenSilent')
  // Arrange: mock useMsal to return instance with acquireTokenSilent resolving { accessToken: 'tok' }
  // Act: call hook's acquireToken()
  // Assert: returns 'tok'

  it('acquireToken falls back to acquireTokenPopup on InteractionRequiredAuthError')
  // Arrange: acquireTokenSilent rejects with InteractionRequiredAuthError; acquireTokenPopup resolves { accessToken: 'popup-tok' }
  // Act: call acquireToken()
  // Assert: returns 'popup-tok'
})
```

### `apiClient` tests

```ts
// src/lib/__tests__/apiClient.test.ts

describe('apiClient', () => {
  it('attaches Authorization Bearer header to requests')
  // Arrange: setMsalInstance with mock that resolves accessToken 'test-token'
  // Act: make any GET request (intercept with MSW or axios-mock-adapter)
  // Assert: request header Authorization === 'Bearer test-token'

  it('redirects to login on 401 response')
  // Arrange: mock server returns 401
  // Act: make a GET request
  // Assert: msalInstance.loginRedirect was called
})
```

### `buildHubConnection` tests

```ts
// src/lib/__tests__/signalrClient.test.ts

describe('buildHubConnection', () => {
  it('creates connection with accessTokenFactory')
  // Arrange: vi.mock('@microsoft/signalr') to capture constructor args
  // Act: buildHubConnection('/hubs/agent', async () => 'tok')
  // Assert: HubConnectionBuilder.withUrl was called with accessTokenFactory option
})
```

### `useAgentHub` tests

```ts
// src/hooks/__tests__/useAgentHub.test.ts

describe('useAgentHub', () => {
  it('starts in disconnected state')
  // Render hook; assert connectionState === 'disconnected'

  it('transitions to connected state after start()')
  // Mock HubConnection.start() resolves; simulate onreconnected or manually set; assert connectionState === 'connected'

  it('cleanup calls connection.stop() on unmount')
  // Render and unmount hook; assert stop() was called on the HubConnection mock
})
```

---

## Files to Create / Modify

```
src/Content/Presentation/Presentation.WebUI/src/
  lib/
    authConfig.ts        ← extend skeleton from section-08
    apiClient.ts         ← new
    signalrClient.ts     ← new
  hooks/
    useAuth.ts           ← new
    useAgentHub.ts       ← new
```

---

## Implementation Details

### `src/lib/authConfig.ts`

Section-08 created a skeleton. Extend it to export:

- `msalConfig: Configuration` — reads `VITE_AZURE_CLIENT_ID` and `VITE_AZURE_TENANT_ID` from `import.meta.env`.
- `loginRequest: PopupRequest` — includes the API scope `api://${import.meta.env.VITE_AZURE_API_CLIENT_ID}/access_as_user`.

The `.env.example` (committed) documents the three required variables: `VITE_AZURE_CLIENT_ID`, `VITE_AZURE_API_CLIENT_ID`, `VITE_AZURE_TENANT_ID`, `VITE_API_BASE_URL`. Actual values go in `.env.local` (gitignored — already in `.gitignore` from section-08).

### `src/hooks/useAuth.ts`

Wraps `useMsal()`. Signature:

```ts
export interface UseAuthReturn {
  account: AccountInfo | null;
  isAuthenticated: boolean;
  acquireToken: () => Promise<string>;
  signOut: () => void;
}

export function useAuth(): UseAuthReturn
```

`acquireToken` implementation:
1. Calls `instance.acquireTokenSilent({ account, scopes: loginRequest.scopes })`.
2. On `InteractionRequiredAuthError`, falls back to `instance.acquireTokenPopup({ account, scopes: loginRequest.scopes })`.
3. Returns `response.accessToken`.

`signOut` calls `instance.logoutRedirect()`.

### `src/lib/apiClient.ts`

Creates an axios instance with `baseURL: import.meta.env.VITE_API_BASE_URL`.

The token acquisition problem: axios interceptors run outside React component trees so `useMsal()` cannot be called directly. Use a module-level setter:

```ts
let _msalInstance: IPublicClientApplication | null = null;
export function setMsalInstance(instance: IPublicClientApplication): void

export const apiClient = axios.create({ baseURL: import.meta.env.VITE_API_BASE_URL });
```

`App.tsx` (from section-08) must call `setMsalInstance(instance)` after obtaining the instance from `useMsal()`.

**Request interceptor:** Calls `_msalInstance.acquireTokenSilent(...)`, falls back to popup on `InteractionRequiredAuthError`, sets `config.headers.Authorization = \`Bearer ${token}\``.

**Response interceptor:** On 401, calls `_msalInstance?.loginRedirect(loginRequest)` and rejects the promise.

### `src/lib/signalrClient.ts`

```ts
export function buildHubConnection(
  path: string,
  getToken: () => Promise<string>
): HubConnection
```

Uses `HubConnectionBuilder` with:
- `.withUrl(path, { accessTokenFactory: getToken })`
- `.withAutomaticReconnect([0, 2000, 10000, 30000])`
- `.configureLogging(LogLevel.Warning)`
- `.build()`

### `src/hooks/useAgentHub.ts`

The central real-time hook. Key design decisions:

1. **`useRef` for the connection** — avoids recreating the connection on re-renders.
2. **`useState` for `connectionState`** — `'disconnected' | 'connecting' | 'connected' | 'reconnecting'`.
3. **React 19 StrictMode safety** — the `useEffect` cleanup must call `connection.stop()` and set a `stopped` flag so the double-invoke in dev mode does not leave orphaned connections. Guard the start call: if already started or stopping, skip.
4. **Event handler registration** — register all `connection.on(...)` handlers before calling `connection.start()`. Deregister in cleanup via `connection.off(...)`.

Signature:

```ts
export interface UseAgentHubReturn {
  connectionState: ConnectionState;
  sendMessage: (conversationId: string, message: string) => Promise<void>;
  startConversation: (agentName: string, conversationId: string) => Promise<void>;
  invokeToolViaAgent: (conversationId: string, toolName: string, args: Record<string, unknown>) => Promise<void>;
  joinGlobalTraces: () => Promise<void>;
  leaveGlobalTraces: () => Promise<void>;
}

export function useAgentHub(): UseAgentHubReturn
```

**SignalR events to handle and their Zustand dispatch targets** (stores defined in sections 10 and 11 — import them; they will exist when the full app compiles):

| Event | Store action |
|---|---|
| `TokenReceived` (token: string) | `useChatStore.getState().appendToken(token)` |
| `TurnComplete` (message: ChatMessage) | `useChatStore.getState().finalizeStream(message)` |
| `Error` (message: string) | `useChatStore.getState().setError(message)` |
| `SpanReceived` (span: SpanData) | `useTelemetryStore.getState().addConversationSpan(activeConversationId, span)` AND `useTelemetryStore.getState().addGlobalSpan(span)` |
| `ConversationHistory` (messages: ChatMessage[]) | `useChatStore.getState().setMessages(messages)` |

**`sendMessage`** calls `connection.invoke('SendMessage', conversationId, message)`.

**`startConversation`** calls `connection.invoke('StartConversation', agentName, conversationId)`.

**`invokeToolViaAgent`** calls `connection.invoke('InvokeToolViaAgent', conversationId, toolName, args)`.

**`joinGlobalTraces` / `leaveGlobalTraces`** call `connection.invoke('JoinGlobalTraces')` / `connection.invoke('LeaveGlobalTraces')`.

---

## Integration with App.tsx (section-08)

After this section, `App.tsx` must be updated to call `setMsalInstance(instance)` from a child component that has access to the MSAL context:

```tsx
// Inside a component rendered under MsalProvider:
const { instance } = useMsal();
useEffect(() => { setMsalInstance(instance); }, [instance]);
```

This is a small modification to the `App.tsx` file created in section-08.

---

## Actual Implementation Notes

### Deviations from plan
- **`accounts[0]` stale closure (code review fix)**: `getToken` inside `useAgentHub` now calls `instance.getAllAccounts()[0]` at call time instead of capturing `accounts[0]` at effect-creation time. Prevents stale account if user re-authenticates.
- **Request interceptor error propagation (code review fix)**: Non-`InteractionRequiredAuthError` errors in the request interceptor are re-thrown (instead of silently swallowed), so requests fail fast rather than sending without a token.
- **`_redirecting` guard (code review fix)**: Response interceptor now guards against infinite 401 redirect loops with a module-level `_redirecting` boolean.
- **`useAgentHub` action methods guard (code review fix)**: All `sendMessage`, `startConversation`, etc. now throw `Error('SignalR connection not established')` when `connectionRef.current` is null, rather than silently no-opping via optional chaining.
- **SignalR start error reporting (code review fix)**: `.catch()` on `connection.start()` now pushes the error message to `useChatStore.getState().setError()` in addition to setting state to `'disconnected'`.
- **`requireEnv()` fail-fast (code review fix)**: `authConfig.ts` now uses a `requireEnv()` helper that throws at startup if `VITE_AZURE_CLIENT_ID`, `VITE_AZURE_TENANT_ID`, or `VITE_AZURE_API_CLIENT_ID` is missing, preventing silent scope degradation.
- **vi.fn() constructor mock**: `HubConnectionBuilder` mock in `signalrClient.test.ts` must use `vi.fn(function() {...})` (not arrow function) to be usable as a constructor with `new`.
- **`useAgentHub` uses only `instance` from `useMsal()`**: `accounts` removed from destructuring since `getToken` resolves the current account dynamically via `instance.getAllAccounts()`.

### Files created
```
src/hooks/useAuth.ts
src/stores/chatStore.ts     (stub — full impl section 10)
src/stores/telemetryStore.ts (stub — full impl section 11)
src/hooks/__tests__/useAuth.test.ts
src/hooks/__tests__/useAgentHub.test.ts
src/lib/__tests__/apiClient.test.ts
src/lib/__tests__/signalrClient.test.ts
```

### Files modified
```
src/lib/apiClient.ts      — replaced stub with token interceptor + _redirecting guard
src/lib/signalrClient.ts  — replaced stub with HubConnectionBuilder factory
src/hooks/useAgentHub.ts  — replaced stub with full SignalR hook
src/lib/authConfig.ts     — scope → VITE_AZURE_API_CLIENT_ID/access_as_user; added requireEnv()
src/app/App.tsx           — added MsalInstanceSync component
tsconfig.app.json         — added src/**/__tests__ to exclude
tsconfig.test.json        — added src/**/__tests__ to include
```

## Verification

After completing this section:

```bash
cd src/Content/Presentation/Presentation.WebUI
npm run build    # TypeScript must compile — no errors
npm test         # All 16 tests pass (8 new + 8 from section-08)
```

Actual test results (16 passed, 9 files):
- `acquireToken returns token from acquireTokenSilent` — PASS
- `acquireToken falls back to acquireTokenPopup on InteractionRequiredAuthError` — PASS
- `attaches Authorization Bearer header to requests` — PASS
- `redirects to login on 401 response` — PASS
- `creates connection with accessTokenFactory` — PASS
- `starts in disconnected state` — PASS
- `transitions to connected state after start()` — PASS
- `cleanup calls connection.stop() on unmount` — PASS
- All 8 section-08 tests — PASS
