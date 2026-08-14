# Presentation.WebUI

End-user chat/agent UI. React SPA, separate stack from the rest of the .NET solution — treat as its own project, not a Presentation-layer C# concern.

## Stack
- React 19, TypeScript, Vite 8, Tailwind 4
- TanStack Query (server state), Zustand (client state), React Router 8, React Hook Form + Zod
- Radix UI / Base UI + shadcn components
- MSAL (`@azure/msal-browser`/`msal-react`) for auth, `@microsoft/signalr` for realtime, `@ag-ui/client` for agent-run streaming
- `react-markdown` + `rehype-highlight`/`rehype-sanitize`/`remark-gfm` for rendering agent output
- Vitest + Testing Library (unit), Playwright (e2e)

## Commands (run from this directory)
- `npm run dev` — dev server
- `npm run dev:all` — dev server + `Presentation.AgentHub` API concurrently
- `npm run build` — `tsc -b && vite build`
- `npm run typecheck` — `tsc -b` only
- `npm run lint` — eslint
- `npm test` / `npm run test:watch` / `npm run test:coverage` — Vitest
- `npm run test:e2e` — Playwright

## Layout
- `src/features/` — feature folders: `agents/`, `chat/` (+ `widgets/`), `commands/`, `config/`, `conversations/`, `mcp/`
- `src/components/` — `ui/` (shadcn-generated primitives), `layout/`, `theme/`
- `src/views/`, `src/stores/`, `src/hooks/`, `src/lib/`, `src/types/`
- Tests are co-located in `__tests__/` subfolders next to the code they cover (`features/chat/__tests__/`, `hooks/__tests__/`, `lib/__tests__/`, etc.), plus a shared `src/test/` for setup/helpers

## Notes
- Backing API is `Presentation.AgentHub` — `npm run dev:all` runs both together.
- `dist/`, `coverage/`, `test-results/`, `node_modules/` are build/test output — never hand-edit, never treat as source when exploring.
