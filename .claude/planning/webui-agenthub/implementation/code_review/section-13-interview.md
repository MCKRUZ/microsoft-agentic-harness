# Code Review Interview: section-13-integration

## Findings Triaged

### Asked User

**Finding #2 [LOW] — Proxy missing `changeOrigin: true`**
- Decision: Apply
- Applied: Expanded `/api` from string shorthand to `{ target, changeOrigin: true }`, added `changeOrigin: true` to `/hubs` entry. Updated proxy test assertions to match object form.

**Finding #1 [MEDIUM] — `.env.example` renamed `VITE_AZURE_CLIENT_ID` → `VITE_AZURE_SPA_CLIENT_ID`**
- Decision: Add migration hint comment
- Applied: Added `# Was: VITE_AZURE_CLIENT_ID (renamed to reflect two-app model)` above `VITE_AZURE_SPA_CLIENT_ID` in `.env.example`

**Finding #3 [LOW] — `cp` command is Unix-only in docs**
- Decision: Add PowerShell variant
- Applied: Updated Step 4 in `docs/azure-ad-setup.md` to show both `cp` (bash) and `Copy-Item` (PowerShell) forms

### Auto-fixed (none — all three were user decisions)

### Let go
- **Finding #4 [LOW]** — `tsc --noEmit` vs `tsc -b`: Minor build time concern, not worth reverting
- **Finding #5 [INFO]** — `dev:all` AgentHub port: Informational, no code change needed; port 5001 is confirmed correct by plan
