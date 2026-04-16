# Section 13: Integration and Developer Workflow

## Overview

This is the final section. It wires both projects together for local development and documents the Azure AD setup required to run the full stack. All prior sections (01–07 for AgentHub, 08–12 for WebUI) must be complete before this section.

**Dependencies:** section-07-agenthub-tests, section-12-webui-tests

**Verification commands:**
- `dotnet build src/AgenticHarness.slnx`
- `dotnet test src/AgenticHarness.slnx`
- `cd src/Content/Presentation/Presentation.WebUI && npm run build`
- `cd src/Content/Presentation/Presentation.WebUI && npm test`

---

## Tests (Write These First)

These are CI-runnable assertions — most are shell-level exit-code checks or config presence checks:

- `npm run build exits 0` — TypeScript compiles, Vite bundles without error
- `dotnet build src/AgenticHarness.slnx exits 0` — zero errors, zero warnings
- `dotnet test src/AgenticHarness.slnx exits 0` — all tests green
- `npm run test:coverage produces coverage >= 80%` — run `vitest run --coverage`, assert threshold
- `Vite proxy config forwards /api/*` — verified by inspecting `vite.config.ts` (a unit test or build output check confirming the proxy block exists and targets `http://localhost:5001`)

The proxy config test can be a simple Vitest test that imports `vite.config.ts` and asserts `server.proxy['/api'].target === 'http://localhost:5001'` and `server.proxy['/hubs'].ws === true`.

---

## Files to Create or Modify

### 1. `src/Content/Presentation/Presentation.WebUI/.env.example`

Committed to source control. Documents every required environment variable with inline comments:

```
VITE_AZURE_SPA_CLIENT_ID=          # SPA app registration client ID
VITE_AZURE_TENANT_ID=              # shared tenant ID
VITE_AZURE_API_CLIENT_ID=          # API app registration client ID (for scope construction)
VITE_API_BASE_URL=http://localhost:5001
```

`.env.local` (gitignored — developer fills in real values) is **not** committed. Add `.env.local` to `.gitignore` if not already present.

### 2. `src/Content/Presentation/Presentation.WebUI/vite.config.ts` — proxy block

Add a `server.proxy` section so that all `/api/*` and `/hubs/*` requests from the dev server are forwarded to the AgentHub backend. No CORS issues in development.

```ts
server: {
  proxy: {
    '/api': 'http://localhost:5001',
    '/hubs': {
      target: 'http://localhost:5001',
      ws: true,
    },
  },
},
```

### 3. `src/Content/Presentation/Presentation.WebUI/package.json` — scripts block

Ensure these scripts are present (add `concurrently` as a dev dependency if not already installed):

```json
"scripts": {
  "dev": "vite",
  "dev:all": "concurrently -n \"API,UI\" -c \"cyan,magenta\" \"dotnet run --project ../Presentation.AgentHub\" \"vite\"",
  "build": "tsc --noEmit && vite build",
  "preview": "vite preview",
  "test": "vitest",
  "test:coverage": "vitest run --coverage",
  "test:ui": "vitest --ui"
}
```

Install: `npm install --save-dev concurrently`

### 4. `src/Content/Presentation/Presentation.AgentHub/appsettings.json`

Ensure placeholder `AzureAd` section exists with a clear comment so the file is self-documenting:

```json
"AzureAd": {
  "TenantId": "YOUR_TENANT_ID",
  "ClientId": "YOUR_API_CLIENT_ID",
  "Audience": "api://YOUR_API_CLIENT_ID"
}
```

Actual development values go in `appsettings.Development.json` or via `dotnet user-secrets set "AzureAd:TenantId" "..."`.

### 5. `docs/azure-ad-setup.md`

Documentation-only file. Content must cover:

1. **Register the API app** in Azure AD (single tenant). Under "Expose an API", add scope `access_as_user`. Note the Application ID URI (`api://{apiClientId}`).
2. **Register the SPA app** in Azure AD. Under "Authentication", add platform "Single-page application" with redirect URI `http://localhost:5173`. Under "API permissions", add delegated permission for the API app's `access_as_user` scope.
3. Copy API app TenantId and ClientId to `appsettings.Development.json` or user-secrets.
4. Copy SPA ClientId, API ClientId, and TenantId to `.env.local`.
5. (Optional) Assign `AgentHub.Traces.ReadAll` app role in the API app manifest for users who need the global traces view.

---

## Architecture Context: Two-App Azure AD Model

The SPA and API use separate Azure AD app registrations:

| Registration | Purpose | Config location |
|---|---|---|
| **API app** (`AgentHub`) | Exposes `access_as_user` scope | `appsettings.json` → `AzureAd:ClientId`, `AzureAd:Audience` |
| **SPA app** (`AgentWebUI`) | Requests the API scope | `.env.local` → `VITE_AZURE_SPA_CLIENT_ID` |

`authConfig.ts` (implemented in section-09) constructs the API scope as:
```ts
`api://${import.meta.env.VITE_AZURE_API_CLIENT_ID}/access_as_user`
```

This means `VITE_AZURE_SPA_CLIENT_ID` is the client ID the SPA uses to authenticate itself, and `VITE_AZURE_API_CLIENT_ID` is the client ID of the backend — they are different values.

---

## Dev Workflow (After Setup)

1. Fill in `.env.local` with Azure AD values from the two app registrations.
2. Set AgentHub user-secrets or `appsettings.Development.json` with AzureAd section.
3. Run `npm run dev:all` from `Presentation.WebUI` — starts AgentHub on port 5001 and Vite on port 5173 concurrently with color-coded output.
4. Browser opens `http://localhost:5173` → MSAL redirects to Azure AD → returns to the SPA with a token.
5. All `/api/*` and `/hubs/*` calls are proxied by Vite — no CORS configuration needed in development.

---

## Build Verification Sequence

Run in this order after all sections are implemented. Each must exit 0 before proceeding:

```bash
# 1. AgentHub build
dotnet build src/AgenticHarness.slnx

# 2. AgentHub tests
dotnet test src/AgenticHarness.slnx

# 3. WebUI TypeScript compile + bundle
cd src/Content/Presentation/Presentation.WebUI
npm install
npm run build

# 4. WebUI tests with coverage
npm run test:coverage
```

Step 5 is manual: `npm run dev:all` — verify both processes start, browser loads, and Azure AD login works end-to-end.

---

## Notes for the Implementer

- The `concurrently` command in `dev:all` references `../Presentation.AgentHub` — this relative path assumes the script is run from the `Presentation.WebUI` directory. Verify the relative path is correct for the actual directory layout.
- `.env.example` must be committed. `.env.local` must be gitignored. Check `.gitignore` at the repo root and/or in the WebUI directory.
- If `vitest` coverage thresholds are not already configured, add a `coverage` block to `vitest.config.ts` (or the coverage config in `vite.config.ts`) with `lines: 80, branches: 80, functions: 80, statements: 80`.
- The `docs/` directory may not exist yet — create it at the repo root.
- `appsettings.json` placeholder values should be obviously fake (not real GUIDs) to avoid confusion with actual credentials.

---

## Implementation Notes (Actual)

**Status:** Complete

### Files Created/Modified
- `src/Content/Presentation/Presentation.WebUI/vite.config.ts` — added `server.proxy` with `/api` and `/hubs` entries (object form with `changeOrigin: true`)
- `src/Content/Presentation/Presentation.WebUI/package.json` — added `dev:all`, updated `build` to `tsc --noEmit`, added `test:ui`; `concurrently` was already in devDependencies
- `src/Content/Presentation/Presentation.WebUI/.env.example` — expanded from 3 lines to full two-app model with inline comments
- `docs/azure-ad-setup.md` — new file; `docs/` directory created at repo root
- `src/Content/Presentation/Presentation.WebUI/src/test/vite-proxy.test.ts` — new Vitest test asserting proxy object structure
- `src/Content/Presentation/Presentation.AgentHub/appsettings.json` — already had AzureAd placeholders; no changes needed
- `vitest.config.ts` — coverage thresholds already present; no changes needed
- `.gitignore` — `.env*` already gitignored at root; no changes needed

### Deviations from Plan
- **Proxy config used object form for `/api`** (not string shorthand) — required to add `changeOrigin: true` per code review
- **`changeOrigin: true` added to both proxy entries** — prevents Host header mismatch issues, not in original plan
- **`.env.example` migration hint added** — `# Was: VITE_AZURE_CLIENT_ID` comment added since the variable was renamed from the old single-app pattern
- **docs step 4 shows PowerShell variant** — `Copy-Item .env.example .env.local` added alongside `cp` for Windows developers

### Test Results
- 20 test files, 57 tests — all passed
- dotnet build: 0 errors
