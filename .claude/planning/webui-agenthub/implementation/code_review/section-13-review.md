# Code Review: section-13-integration

## Summary
Clean integration section. No security issues. Five findings below — none block shipping.

---

## Findings

### 1. [MEDIUM] `.env.example` renames `VITE_AZURE_CLIENT_ID` → `VITE_AZURE_SPA_CLIENT_ID`
**File:** `src/Content/Presentation/Presentation.WebUI/.env.example`

The old example had `VITE_AZURE_CLIENT_ID`. Any developer with an existing `.env.local` based on the old template will get a silent auth failure because `authConfig.ts` now reads `VITE_AZURE_SPA_CLIENT_ID`. There's no warning in the diff.

**Recommendation:** Add a note to `docs/azure-ad-setup.md` (or a comment in `.env.example`) that the variable was renamed from `VITE_AZURE_CLIENT_ID`. A `# Was: VITE_AZURE_CLIENT_ID (renamed in section-13)` comment in `.env.example` would catch this at config time.

---

### 2. [LOW] Vite proxy missing `changeOrigin: true`
**File:** `src/Content/Presentation/Presentation.WebUI/vite.config.ts`

```ts
'/api': 'http://localhost:5001',
'/hubs': { target: 'http://localhost:5001', ws: true },
```

Without `changeOrigin: true`, the proxy forwards the original `Host` header (`localhost:5173`) to the backend. ASP.NET Core's `UseHttpsRedirection` and some CORS configurations check the Host header. This works for localhost-to-localhost but is a common gotcha when the ports differ or when HTTPS is involved.

**Recommendation:** Add `changeOrigin: true` to both proxy entries for consistency and to avoid surprises. Low risk to apply.

```ts
'/api': { target: 'http://localhost:5001', changeOrigin: true },
'/hubs': { target: 'http://localhost:5001', ws: true, changeOrigin: true },
```

Note: expanding `/api` from string shorthand to object form is required to add `changeOrigin`.

---

### 3. [LOW] `cp .env.example .env.local` in docs is Unix-only
**File:** `docs/azure-ad-setup.md`, Step 4

The shell command `cp .env.example .env.local` won't work in PowerShell or CMD. This project targets Windows developers (Windows 11, PowerShell in CLAUDE.md).

**Recommendation:** Show both:
```bash
# bash / Git Bash
cp .env.example .env.local

# PowerShell
Copy-Item .env.example .env.local
```

---

### 4. [LOW] `tsc --noEmit` loses incremental build benefit
**File:** `src/Content/Presentation/Presentation.WebUI/package.json`

Changed from `tsc -b` (builds project references, uses `.tsbuildinfo` for incremental compilation) to `tsc --noEmit && vite build`. For a single-project SPA this difference is minor, but `tsc -b` is the Vite scaffold default for a reason — it's faster on repeat builds.

**Recommendation:** Consider keeping `tsc -b --noEmit && vite build` to preserve the type-check-only + incremental behavior. Or leave as-is — it's a minor CI build time concern.

---

### 5. [INFO] `dev:all` requires AgentHub to not have HTTPS enforced
**File:** `src/Content/Presentation/Presentation.WebUI/package.json`

`dotnet run` starts AgentHub with the default `launchSettings.json` profile, which may launch on HTTPS (5001 HTTPS, or 7001 depending on profile). If the backend starts on `https://localhost:7001` but the proxy targets `http://localhost:5001`, all API calls will silently fail with connection refused.

**Recommendation:** Verify the AgentHub `launchSettings.json` HTTP port matches the proxy target, or document which `--launch-profile` to use. No code change needed if port 5001 is correct.

---

## Verdict
**No blockers.** Finding #2 (`changeOrigin`) is the only one worth applying before commit — it's a one-line-per-entry change and prevents a class of proxy debugging pain. Findings #1 and #3 are documentation improvements. Findings #4 and #5 are informational.
