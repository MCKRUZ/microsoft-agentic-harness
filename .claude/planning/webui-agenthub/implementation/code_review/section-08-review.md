# Section 08 - WebUI App Shell: Code Review

**Reviewer:** claude-code-reviewer
**Date:** 2026-04-15
**Scope:** Vite + React 19 + TypeScript app shell (AppShell, Header, SplitPanel, ThemeProvider, stubs, tests)

---

## Summary

Solid foundational shell. Clean component decomposition, good provider layering, proper MSAL integration pattern, and reasonable test coverage for an app shell section. The diff also cleans up package.json (moving concurrently/shadcn to devDependencies) and adds Babel packages as dev-only -- both good hygiene moves.

**Verdict: WARNING -- no CRITICAL or HIGH issues. Several MEDIUM items worth addressing before section-09 builds on top.**

---

## CRITICAL

None.

---

## HIGH

None.

---

## MEDIUM

### M1. ThemeProvider getInitialTheme calls localStorage/matchMedia without SSR guard

**File:** src/components/theme/ThemeProvider.tsx:10-13

getInitialTheme() is called as the initial value of useState, which is fine for CSR. However, because it references localStorage and window.matchMedia directly (not behind a typeof window guard), it will throw if this module is ever imported in an SSR or Node context.

The test setup mocks matchMedia globally, so tests pass today. But any future test that imports ThemeProvider before setup.ts runs will break.

**Fix:** Add a guard -- check typeof window before accessing localStorage or matchMedia.

---

### M2. SplitPanel divider has no keyboard accessibility

**File:** src/components/layout/SplitPanel.tsx:36-40

The divider div has onMouseDown and aria-hidden=true, meaning keyboard users cannot resize panels at all. The element should use role=separator, aria-orientation=vertical, tabIndex=0, and handle ArrowLeft/ArrowRight key events. This is a WCAG 2.1 AA requirement.

---

### M3. authConfig.ts constructs PublicClientApplication with potentially empty clientId

**File:** src/lib/authConfig.ts:5-18

If VITE_AZURE_CLIENT_ID is not set, clientId becomes empty string and MSAL throws a cryptic error. Add a fail-fast guard with a clear error message pointing to .env.example.

---

### M4. toggleTheme is recreated on every render

**File:** src/components/theme/ThemeProvider.tsx:29

The toggleTheme function creates a new reference on each render. Wrap in useCallback to prevent unnecessary re-renders of memoized consumers.

---

### M5. Magic number in queryClient staleTime

**File:** src/lib/queryClient.ts:6

staleTime: 1000 * 60 * 5 should be extracted to a named constant like FIVE_MINUTES_MS.

---

### M6. SplitPanel mouse event listeners not cleaned up on unmount

**File:** src/components/layout/SplitPanel.tsx:12-27

If the component unmounts during an active drag, mousemove and mouseup listeners on document will leak. This is a real risk in React 19 StrictMode (double mount/unmount in dev). Track listeners in a ref and clean up via useEffect return.

---

## LOW

### L1. Duplicate useTheme export path

**Files:** src/hooks/useTheme.ts:1 and src/components/theme/ThemeProvider.tsx:38

useTheme is exported from both files. Pick one canonical import path to avoid inconsistency.

---

### L2. role=main is redundant on main element

**File:** src/components/layout/SplitPanel.tsx:33

The main element already has an implicit ARIA role of main. Remove the explicit role=main.

---

### L3. Test utils renderWithProviders does not include MsalProvider

**File:** src/test/utils.tsx:21-34

The Wrapper includes MemoryRouter, QueryClientProvider, and ThemeProvider, but not MsalProvider. Components calling useMsal() must mock it manually. Consider adding a mock MsalProvider to the test wrapper.

---

### L4. Catch-all route duplicates the index route

**File:** src/app/router.tsx:31-34

path=* already matches /, making the path=/ route redundant. Consider using a layout route with Outlet for future extensibility.

---

### L5. apiClient.ts baseURL could be undefined

**File:** src/lib/apiClient.ts:5

Axios treats undefined baseURL as relative-to-origin. Add a comment explaining this is intentional.

---

## POSITIVES

- **Clean provider composition**: Providers layers MSAL > QueryClient > Theme correctly. Easy to extend.
- **Good dependency hygiene**: concurrently and shadcn moved to devDependencies, Babel packages marked dev-only.
- **Intentional stubs with clear markers**: signalrClient.ts and useAgentHub.ts have explicit section-09 replacement comments.
- **Proper void handling on async event handlers**: void instance.loginRedirect() correctly handles floating promises.
- **Theme persistence + system preference fallback**: getInitialTheme() checks localStorage first, then prefers-color-scheme. Correct priority order.
- **CSS approach is sound**: data-theme attribute with @custom-variant and backward compat with .dark class.
- **Test utilities**: renderWithProviders with fresh QueryClient per test prevents pollution. Retry disabled for determinism.
- **Typed interfaces for API and SignalR**: types/api.ts and types/signalr.ts establish contracts early.
- **vi.hoisted() pattern in App.test.tsx**: Correctly handles Vitest module mock timing.
