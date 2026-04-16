# Section 8: Presentation.WebUI — Project Setup and App Shell

## Overview

This section scaffolds the `Presentation.WebUI` Vite + React 19 + TypeScript project, configures Tailwind CSS and shadcn/ui, establishes the complete folder structure, and implements the app shell layout. After this section the app loads in the browser, shows an Azure AD login prompt (via MSAL), and after authentication shows an empty two-panel layout ready for later sections to populate.

**Depends on:** section-01-scaffolding (Vite project already created, added to solution)
**Can parallelize with:** section-02-agenthub-core

**Verify with:** `cd src/Content/Presentation/Presentation.WebUI && npm test`

---

## Tests First

Write these tests before implementing. All live in `src/test/` using Vitest + React Testing Library.

```
src/test/setup.ts         # Vitest setup — import @testing-library/jest-dom
src/test/utils.tsx        # renderWithProviders helper (mock MSAL + QueryClient + Router)
src/__tests__/App.test.tsx
src/__tests__/SplitPanel.test.tsx
src/__tests__/Header.test.tsx
src/__tests__/ThemeProvider.test.tsx
```

### Test stubs to write first

**`App.test.tsx`**
- `App renders without crashing when MSAL is in authenticated state (mock)` — mock `@azure/msal-react` so `AuthenticatedTemplate` renders its children; assert the app shell mounts without error.
- `App renders login redirect when MSAL is not authenticated` — mock MSAL so `UnauthenticatedTemplate` is active; assert a login button is present.

**`SplitPanel.test.tsx`**
- `SplitPanel renders left and right children` — pass `left={<div>Left</div>}` and `right={<div>Right</div>}`; assert both texts appear.
- `SplitPanel left panel is accessible (has landmark role or aria-label)` — assert the left panel has `role="main"` or `aria-label`.

**`Header.test.tsx`**
- `Header renders app name` — assert the string "AgentHub" (or whatever the configured app name is) appears in the DOM.

**`ThemeProvider.test.tsx`**
- `ThemeProvider applies data-theme="dark" to html element when dark mode selected` — call the toggle; assert `document.documentElement.dataset.theme === 'dark'`.
- `ThemeProvider persists theme selection to localStorage` — toggle to dark; assert `localStorage.getItem('theme') === 'dark'`.

### Mock pattern for MSAL

In `src/test/utils.tsx`, mock `@azure/msal-react` at the module level:

```typescript
// Stub — exact impl determined by implementer
vi.mock('@azure/msal-react', () => ({
  MsalProvider: ({ children }: { children: React.ReactNode }) => <>{children}</>,
  AuthenticatedTemplate: ({ children }: { children: React.ReactNode }) => <>{children}</>,
  UnauthenticatedTemplate: () => null,
  useMsal: () => ({ instance: mockMsalInstance, accounts: [mockAccount] }),
}));
```

---

## Implementation

### 1. Install Dependencies

From `src/Content/Presentation/Presentation.WebUI/`:

```bash
npm install @azure/msal-browser @azure/msal-react
npm install @tanstack/react-query
npm install react-router-dom
npm install axios
npm install zustand
npm install react-hook-form @hookform/resolvers zod
npm install react-window
npm install @microsoft/signalr
npm install tailwindcss @tailwindcss/vite
npm install class-variance-authority clsx tailwind-merge lucide-react
npm install --save-dev vitest @vitejs/plugin-react jsdom @testing-library/react @testing-library/jest-dom @testing-library/user-event
```

Initialize shadcn/ui:
```bash
npx shadcn@latest init
```
Select: TypeScript, default style, CSS variables for color. Then add components:
```bash
npx shadcn@latest add button input badge tabs separator textarea
```

### 2. TypeScript and Vite Config

**`tsconfig.json`** — enable strict mode:
```json
{
  "compilerOptions": {
    "strict": true,
    "target": "ES2022",
    "lib": ["ES2022", "DOM", "DOM.Iterable"],
    "module": "ESNext",
    "moduleResolution": "bundler",
    "jsx": "react-jsx",
    "baseUrl": ".",
    "paths": { "@/*": ["./src/*"] },
    "noEmit": true
  }
}
```

**`vite.config.ts`** — add React plugin, Tailwind plugin, test config, and dev proxy:
```typescript
// Stub — add defineConfig with:
// plugins: [react(), tailwindcss()]
// resolve.alias: { '@': '/src' }
// test: { globals: true, environment: 'jsdom', setupFiles: ['./src/test/setup.ts'] }
// server.proxy: { '/api': 'http://localhost:5001', '/hubs': { ws: true, target: 'http://localhost:5001' } }
```

**`tailwind.config.ts`** — `darkMode: ['attr', '[data-theme]']`, content glob covering all `src/**/*.{ts,tsx}`.

### 3. Folder Structure

Create all directories and empty index files as needed:

```
src/
  app/
    App.tsx
    main.tsx
    providers.tsx
    router.tsx
  components/
    layout/
      AppShell.tsx
      SplitPanel.tsx
      Header.tsx
    ui/                  ← shadcn/ui copied components land here
    theme/
      ThemeProvider.tsx
  features/              ← empty, populated in sections 10–11
  hooks/
    useAgentHub.ts       ← stub only in this section
    useTheme.ts
  lib/
    authConfig.ts
    apiClient.ts         ← stub only in this section
    queryClient.ts
    signalrClient.ts     ← stub only in this section
  stores/
    appStore.ts
  types/
    api.ts
    signalr.ts
  test/
    setup.ts
    utils.tsx
```

### 4. File Implementations

**`src/app/main.tsx`**
Standard Vite entry point. `ReactDOM.createRoot(document.getElementById('root')!).render(<App />)`. Import global CSS.

**`src/lib/authConfig.ts`**
Export `msalConfig: Configuration` reading `VITE_AZURE_CLIENT_ID` and `VITE_AZURE_TENANT_ID` from `import.meta.env`. Export `loginRequest: PopupRequest` with scope `api://${import.meta.env.VITE_AZURE_CLIENT_ID}/.default`. Export a `PublicClientApplication` instance as `msalInstance`.

**`src/lib/queryClient.ts`**
Create and export a `QueryClient` instance with default options (`staleTime: 1000 * 60 * 5`).

**`src/components/theme/ThemeProvider.tsx`**
- State: `theme: 'light' | 'dark'`, initialized from `localStorage.getItem('theme')` falling back to `window.matchMedia('(prefers-color-scheme: dark)').matches`.
- Effect: sets `document.documentElement.dataset.theme = theme` and writes to localStorage on change.
- Exposes `ThemeContext` with `{ theme, toggleTheme }`.
- Export `ThemeProvider` component and `useTheme` hook.

**`src/hooks/useTheme.ts`**
Re-export or thin wrapper around `useContext(ThemeContext)` with a guard for undefined context.

**`src/components/layout/SplitPanel.tsx`**
Props: `left: React.ReactNode`, `right: React.ReactNode`. CSS Grid layout with `grid-template-columns: 40fr 4px 60fr` (or a user-draggable divider). Left panel has `role="main"` and `aria-label="Chat panel"`. Right panel has `aria-label="Details panel"`. The divider is a `div` with `cursor: col-resize` and `mousedown` drag logic updating a CSS custom property `--split-pos` on the container.

**`src/components/layout/Header.tsx`**
Props: none (reads from context/store). Structure:
- Left: app name text + agent selector `<select>` placeholder (disabled, populated in section 11).
- Right: theme toggle `<button>` (sun/moon icon from `lucide-react`) + username from `useMsal().accounts[0]?.name` + sign-out `<button>` calling `instance.logoutRedirect()`.

**`src/components/layout/AppShell.tsx`**
Full viewport layout: `flex flex-col h-screen`. Header (64px fixed height). Content: `flex-1 overflow-hidden`. Content renders `<SplitPanel left={<div />} right={<div />} />` — placeholder divs replaced in sections 10–11.

**`src/app/router.tsx`**
```typescript
// Stub — export Routes with:
// "/" → <AppShell />  (wrapped in AuthenticatedTemplate)
// login redirect rendered from UnauthenticatedTemplate
```

**`src/app/providers.tsx`**
Composes all providers in order: `MsalProvider` → `QueryClientProvider` → `ThemeProvider`.

**`src/app/App.tsx`**
```typescript
// Compose: <Providers><Router /></Providers>
// Inside Router: AuthenticatedTemplate → <AppShell />
//                UnauthenticatedTemplate → <LoginView /> (simple centered login button)
```

### 5. Stubs for Later Sections

These files must exist and export correctly typed (but non-functional) stubs so the app compiles:

**`src/lib/apiClient.ts`** — export a bare `axios.create({ baseURL: import.meta.env.VITE_API_BASE_URL })` instance. Token interceptor added in section 9.

**`src/lib/signalrClient.ts`** — export a stub `buildHubConnection` that throws `new Error('Not implemented')`. Replaced in section 9.

**`src/hooks/useAgentHub.ts`** — export a stub hook returning empty no-op functions and `connectionState: 'disconnected'`. Replaced in section 9.

**`src/stores/appStore.ts`** — export a minimal Zustand store `{ selectedAgent: null, setSelectedAgent: (name: string) => void }`.

**`src/types/api.ts`** — export empty placeholder interfaces `AgentInfo`, `ConversationRecord`. Extended in later sections.

**`src/types/signalr.ts`** — export `SpanData` interface (full definition needed now; copy from section 11 plan):
```typescript
interface SpanData {
  name: string;
  traceId: string;
  spanId: string;
  parentSpanId: string | null;
  conversationId: string | null;
  startTime: string;
  durationMs: number;
  status: 'unset' | 'ok' | 'error';
  statusDescription?: string;
  kind: string;
  sourceName: string;
  tags: Record<string, string>;
}
```

### 6. Environment Files

**`.env.example`** (committed to git):
```
VITE_AZURE_CLIENT_ID=your-spa-app-client-id
VITE_AZURE_TENANT_ID=your-tenant-id
VITE_API_BASE_URL=http://localhost:5001
```

**`.env.local`** (gitignored) — implementer fills in actual values.

**`.gitignore`** — ensure `.env.local` is listed.

### 7. Test Setup

**`src/test/setup.ts`**
```typescript
import '@testing-library/jest-dom';
// Any global mocks needed across all tests
```

**`src/test/utils.tsx`**
Export `renderWithProviders(ui: React.ReactElement)` — wraps with mock MSAL provider, a fresh `QueryClient` with no retries (`retry: false`), and `MemoryRouter`. This is the standard render helper used by all WebUI tests.

---

## Actual Implementation Notes

### Deviations from plan
- **MSAL v5 `CacheOptions`**: `storeAuthStateInCookie` was removed in MSAL Browser v5. Omitted from `authConfig.ts`.
- **Tailwind dark mode**: Used `@custom-variant dark (&:where([data-theme="dark"], [data-theme="dark"] *))` in CSS (v4 syntax). No `tailwind.config.ts` needed.
- **`react-router-dom` missing from scaffolding**: Installed in this section (`npm install react-router-dom`).
- **`src/App.tsx`**: Replaced with `export { default } from './app/App'` re-export so `src/main.tsx` entry point remains stable.
- **SplitPanel divider accessibility (code review fix)**: Added `role="separator"`, `tabIndex={0}`, `onKeyDown` handler (ArrowLeft/ArrowRight to resize, +Shift for 10% steps). Replaced `aria-hidden` with proper ARIA labeling.
- **SplitPanel drag listener leak (code review fix)**: Added `dragCleanupRef` + `useEffect` cleanup to remove dangling document listeners if component unmounts mid-drag.

### Files created
```
src/app/App.tsx, providers.tsx, router.tsx
src/components/layout/AppShell.tsx, Header.tsx, SplitPanel.tsx
src/components/theme/ThemeProvider.tsx
src/hooks/useTheme.ts, useAgentHub.ts (stub)
src/lib/authConfig.ts, apiClient.ts (stub), queryClient.ts, signalrClient.ts (stub)
src/stores/appStore.ts
src/types/api.ts, signalr.ts
src/test/utils.tsx
src/__tests__/App.test.tsx, Header.test.tsx, SplitPanel.test.tsx, ThemeProvider.test.tsx
.env.example
```

### Files modified
```
src/App.tsx — replaced Vite starter with re-export
src/main.tsx — import updated to ./app/App
src/test/setup.ts — added jest-dom import + matchMedia mock
src/index.css — updated dark custom-variant to data-theme attribute
tsconfig.app.json — added src/__tests__ to exclude list
```

## Verification

After completing this section:

```bash
cd src/Content/Presentation/Presentation.WebUI
npm run build    # TypeScript must compile, Vite must bundle — no errors
npm test         # All 8 tests pass (7 new + 1 scaffold)
```

Actual test results (8 passed, 5 files):
- `App renders without crashing when MSAL is in authenticated state (mock)` — PASS
- `App renders login redirect when MSAL is not authenticated` — PASS
- `SplitPanel renders left and right children` — PASS
- `SplitPanel left panel is accessible (has landmark role or aria-label)` — PASS
- `Header renders app name` — PASS
- `ThemeProvider applies data-theme="dark" to html element when dark mode selected` — PASS
- `ThemeProvider persists theme selection to localStorage` — PASS
- `scaffold test (section-01)` — PASS

No dotnet commands needed — this section is WebUI only.
