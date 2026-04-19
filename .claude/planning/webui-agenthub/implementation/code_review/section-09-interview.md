# Code Review Interview — section-09-msal-auth

## Summary
Review found 2 CRITICAL, 2 HIGH, 3 MEDIUM, 2 LOW issues. All CRITICAL/HIGH and most MEDIUM issues auto-fixed; remainder let go with rationale.

---

## Auto-Fixes Applied

### C1 — Request interceptor silently swallows non-InteractionRequired errors
**Finding:** Any MSAL error other than `InteractionRequiredAuthError` is swallowed, causing requests to proceed without a token (silent auth failure leaking unauthenticated requests).

**Fix:** Added `else { throw error; }` to re-throw unknown errors, ensuring requests fail fast rather than sending without a token.

**Files changed:** `src/lib/apiClient.ts`

### C2 — 401 response interceptor can trigger infinite redirect loop
**Finding:** No guard prevents multiple concurrent calls to `loginRedirect`. MSAL throws `interaction_in_progress` on re-entry; that error was unhandled.

**Fix:** Added module-level `_redirecting` boolean guard; sets to `true` on first redirect, never triggers a second.

**Files changed:** `src/lib/apiClient.ts`

### H1 — `useAgentHub` connection start error silently discarded
**Finding:** `.catch(() => { ... })` swallowed the error entirely — user sees "disconnected" with no diagnostic.

**Fix:** Catch block now passes error message to `useChatStore.getState().setError()`.

**Files changed:** `src/hooks/useAgentHub.ts`

### H2 — Action methods silently no-op when connection is null
**Finding:** `connectionRef.current?.invoke(...)` resolved to `undefined` on disconnect, giving caller false success.

**Fix:** All action methods now check `connectionRef.current` and throw `Error('SignalR connection not established')` if null.

**Files changed:** `src/hooks/useAgentHub.ts`

### M1 — Stale `accounts[0]` captured at effect closure time
**Finding:** If user signs out/in with a different account, `getToken` closure uses the original account object.

**Fix:** `getToken` now calls `instance.getAllAccounts()[0]` at call time, not closure time.

**Files changed:** `src/hooks/useAgentHub.ts`

### M2 — `authConfig.ts` env vars silently degrade to empty/broken scopes
**Finding:** Missing `VITE_AZURE_API_CLIENT_ID` produces scope `api:///access_as_user` — valid syntax, wrong semantics, confusing MSAL errors. Section-08 code review explicitly deferred this validation to section-09.

**Fix:** Added `requireEnv()` helper that throws at module init time if a required env var is absent.

**Files changed:** `src/lib/authConfig.ts`

### M3 — Cleanup test `waitFor(() => {})` is a no-op
**Finding:** Empty `waitFor` resolves immediately, so cleanup test only covers the connecting phase rather than a fully-established connection.

**Fix:** `waitFor` now awaits `connectionState === 'connected'` before unmounting.

**Files changed:** `src/hooks/__tests__/useAgentHub.test.ts`

---

## Decisions to Let Go

| Finding | Decision | Rationale |
|---------|----------|-----------|
| M4 — Missing test: non-401 errors should not trigger loginRedirect | Let go | Section plan specifies exactly 2 apiClient tests. Adding a third is scope expansion; section-12 builds comprehensive test coverage. |
| LOW — Magic reconnect interval numbers `[0, 2000, 10000, 30000]` | Let go | Values are self-documenting exponential backoff. A named constant for a single callsite adds indirection without clarity. |
| LOW — chatStore stub no-ops silently lose data | Let go | Explicitly marked with "Full implementation in section 10" comment. Acceptable stub state; no-console-log rule applies. |
