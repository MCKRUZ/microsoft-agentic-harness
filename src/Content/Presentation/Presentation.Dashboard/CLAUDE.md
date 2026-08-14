# Presentation.Dashboard

Operator-facing metrics/observability dashboard. React SPA, separate stack from the rest of the .NET solution — treat as its own project, not a Presentation-layer C# concern.

## Stack
- React 19, TypeScript, Vite 8, Tailwind 4
- TanStack Query (server state), Zustand (client state), React Router 8
- Radix UI + shadcn components, Recharts for charts
- MSAL (`@azure/msal-browser`/`msal-react`) for auth, `@microsoft/signalr` for realtime, `@ag-ui/client` for agent-run streaming
- Vitest + Testing Library (unit), Playwright (e2e)

## Commands (run from this directory)
- `npm run dev` — dev server on port 5174
- `npm run dev:all` — dev server + `Presentation.AgentHub` API concurrently
- `npm run build` — `tsc -b && vite build`
- `npm run typecheck` — `tsc -b` only
- `npm run lint` — eslint
- `npm test` / `npm run test:watch` / `npm run test:coverage` — Vitest
- `npm run test:e2e` — Playwright

## Layout
- `src/routes/` — one folder per dashboard page (Overview, Cost, Spend, Budget, Tokens, Quality, Safety, Governance, Rag, Evals, Sessions, Tools, Catalog, Pulse, DesignSystem)
- `src/components/` — grouped by domain: `agent/`, `charts/`, `context/`, `layout/`, `metrics/`, `panels/`, `primitives/`, `theme/`
- `src/api/` — API client code; `src/realtime/` — SignalR wiring; `src/auth/` — MSAL setup
- `src/stores/` — Zustand stores; `src/hooks/`, `src/lib/`, `src/config/`
- `src/test/` — shared test helpers (`helpers/`) and mocks (`mocks/`)

## Notes
- Backing API is `Presentation.AgentHub` — `npm run dev:all` runs both together.
- `dist/`, `coverage/`, `test-results/`, `node_modules/` are build/test output — never hand-edit, never treat as source when exploring.
