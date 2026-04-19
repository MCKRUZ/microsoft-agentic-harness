# Code Review Interview — section-08-webui-shell

## Summary
Review found 0 CRITICAL, 0 HIGH issues. 6 MEDIUM, 5 LOW. Two MEDIUM issues auto-fixed; remainder let go with rationale.

---

## Auto-Fixes Applied

### M6 — SplitPanel drag listener leak on unmount
**Finding:** If the component unmounts while a drag is in progress (mouse button held), the `mousemove`/`mouseup` listeners on `document` remain attached.

**Fix:** Added `dragCleanupRef` to track active drag cleanup. A `useEffect` with empty deps calls `dragCleanupRef.current?.()` on unmount, removing any dangling listeners. React StrictMode safe.

**Files changed:** `src/components/layout/SplitPanel.tsx`

### M2 — SplitPanel divider not keyboard-accessible (WCAG 2.1 AA)
**Finding:** Divider had `aria-hidden="true"` and no keyboard support. Users who navigate via keyboard have no way to resize panels.

**Fix:** Changed divider to `role="separator"`, `tabIndex={0}`, `aria-label="Resize panels"`. Added `onKeyDown` handler — ArrowLeft/ArrowRight move split by 2% (10% with Shift). Removed `aria-hidden`.

**Files changed:** `src/components/layout/SplitPanel.tsx`

---

## Decisions to Let Go

| Finding | Decision | Rationale |
|---------|----------|-----------|
| M1 — SSR guard in `getInitialTheme` | Let go | This is a Vite SPA. There is no SSR path. The guard would be dead code. |
| M3 — Empty `clientId` fail-fast guard | Let go | Section-09 (msal-auth) handles MSAL initialization and env validation. Adding it here would duplicate that work. |
| M4 — `toggleTheme` needs `useCallback` | Let go | YAGNI. ThemeProvider renders at most once per theme change. Premature optimization. |
| M5 — Magic number `1000 * 60 * 5` | Let go | The expression is self-documenting. A named constant would add indirection for one callsite. |
| LOW — Redundant `role="main"` | Let go | Explicitly required by `SplitPanel.test.tsx` (`getByRole('main')`). Removing it breaks the test. |
| LOW — Duplicate `useTheme` export paths | Let go | Section plan specifies both export locations. Re-export is the standard barrel pattern. |
| LOW — Missing MsalProvider in `renderWithProviders` | Let go | Design decision: MSAL is mocked at the module level per-test-file. Adding MsalProvider to test utils would require a mock instance in the helper, coupling it to authConfig. |
| LOW — Redundant catch-all `*` route | Let go | SPA behavior for deep-links. Will be revisited if routing becomes more complex in section-13. |
| LOW — Undocumented `undefined` baseURL | Let go | `apiClient.ts` is a stub with a comment indicating section-09 will add the interceptor. The behavior is expected for this phase. |
