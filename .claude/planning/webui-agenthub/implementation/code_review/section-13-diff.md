diff --git a/docs/azure-ad-setup.md b/docs/azure-ad-setup.md
new file mode 100644
index 0000000..2a39c68
--- /dev/null
+++ b/docs/azure-ad-setup.md
@@ -0,0 +1,103 @@
+# Azure AD Setup Guide
+
+This guide describes how to configure the two Azure AD app registrations required to run AgentHub and the WebUI together.
+
+## Architecture: Two-App Model
+
+| Registration | Purpose | Config location |
+|---|---|---|
+| **AgentHub** (API app) | Exposes `access_as_user` scope | `appsettings.Development.json` → `AzureAd` |
+| **AgentWebUI** (SPA app) | Requests the API scope | `.env.local` → `VITE_AZURE_*` |
+
+`authConfig.ts` constructs the API scope as:
+```
+api://{VITE_AZURE_API_CLIENT_ID}/access_as_user
+```
+
+`VITE_AZURE_SPA_CLIENT_ID` is the client ID the SPA uses to authenticate itself. `VITE_AZURE_API_CLIENT_ID` is the client ID of the backend. They are **different values**.
+
+---
+
+## Step 1: Register the API App (AgentHub)
+
+1. Go to [Azure Portal → App registrations](https://portal.azure.com/#view/Microsoft_AAD_RegisteredApps) → **New registration**.
+2. Name: `AgentHub`, Supported account type: single tenant.
+3. Under **Expose an API**:
+   - Set the Application ID URI to `api://{clientId}` (Azure proposes this automatically).
+   - Add scope: `access_as_user` (Admins and users can consent).
+4. Note the **Application (client) ID** and **Directory (tenant) ID** — you need both.
+5. (Optional) Under **App roles**, add role `AgentHub.Traces.ReadAll` for users who need the global traces view.
+
+---
+
+## Step 2: Register the SPA App (AgentWebUI)
+
+1. Go to **App registrations** → **New registration**.
+2. Name: `AgentWebUI`, Supported account type: single tenant.
+3. Under **Authentication**:
+   - Add platform: **Single-page application**.
+   - Redirect URI: `http://localhost:5173` (Vite dev server default).
+4. Under **API permissions**:
+   - Add permission → **My APIs** → select `AgentHub`.
+   - Choose delegated permission `access_as_user`.
+   - Grant admin consent if your tenant requires it.
+5. Note the **Application (client) ID** — this is your SPA client ID.
+
+---
+
+## Step 3: Configure AgentHub Backend
+
+Set the AzureAd values via `dotnet user-secrets` (recommended for development):
+
+```bash
+cd src/Content/Presentation/Presentation.AgentHub
+
+dotnet user-secrets set "AzureAd:TenantId"  "<your-tenant-id>"
+dotnet user-secrets set "AzureAd:ClientId"  "<api-app-client-id>"
+dotnet user-secrets set "AzureAd:Audience"  "api://<api-app-client-id>"
+```
+
+Or add an `appsettings.Development.json` (gitignored):
+
+```json
+{
+  "AzureAd": {
+    "TenantId": "<your-tenant-id>",
+    "ClientId": "<api-app-client-id>",
+    "Audience": "api://<api-app-client-id>"
+  }
+}
+```
+
+---
+
+## Step 4: Configure the WebUI
+
+Copy `.env.example` to `.env.local` and fill in the values:
+
+```bash
+cp .env.example .env.local
+```
+
+```
+VITE_AZURE_SPA_CLIENT_ID=<spa-app-client-id>
+VITE_AZURE_TENANT_ID=<your-tenant-id>
+VITE_AZURE_API_CLIENT_ID=<api-app-client-id>
+VITE_API_BASE_URL=http://localhost:5001
+```
+
+`.env.local` is gitignored — never commit it.
+
+---
+
+## Step 5: Run the Full Stack
+
+From `src/Content/Presentation/Presentation.WebUI`:
+
+```bash
+npm run dev:all
+```
+
+This starts AgentHub on port 5001 and Vite on port 5173 concurrently with color-coded output.
+
+Open `http://localhost:5173`. MSAL redirects to Azure AD → returns with a token. All `/api/*` and `/hubs/*` calls are proxied by Vite — no CORS configuration needed in development.
diff --git a/src/Content/Presentation/Presentation.WebUI/.env.example b/src/Content/Presentation/Presentation.WebUI/.env.example
index 3eecd54..807f1f0 100644
--- a/src/Content/Presentation/Presentation.WebUI/.env.example
+++ b/src/Content/Presentation/Presentation.WebUI/.env.example
@@ -1,3 +1,15 @@
-VITE_AZURE_CLIENT_ID=your-spa-app-client-id
-VITE_AZURE_TENANT_ID=your-tenant-id
+# Copy this file to .env.local and fill in real values from your Azure AD app registrations.
+# .env.local is gitignored — never commit real credentials.
+
+# SPA app registration client ID (the "AgentWebUI" app in Azure AD)
+VITE_AZURE_SPA_CLIENT_ID=
+
+# Shared tenant ID (same for both app registrations)
+VITE_AZURE_TENANT_ID=
+
+# API app registration client ID (the "AgentHub" app in Azure AD)
+# Used to construct the API scope: api://{VITE_AZURE_API_CLIENT_ID}/access_as_user
+VITE_AZURE_API_CLIENT_ID=
+
+# Base URL of the AgentHub backend (proxied by Vite in development)
 VITE_API_BASE_URL=http://localhost:5001
diff --git a/src/Content/Presentation/Presentation.WebUI/package.json b/src/Content/Presentation/Presentation.WebUI/package.json
index bf4fc73..5e4d30c 100644
--- a/src/Content/Presentation/Presentation.WebUI/package.json
+++ b/src/Content/Presentation/Presentation.WebUI/package.json
@@ -5,12 +5,14 @@
   "type": "module",
   "scripts": {
     "dev": "vite",
-    "build": "tsc -b && vite build",
+    "dev:all": "concurrently -n \"API,UI\" -c \"cyan,magenta\" \"dotnet run --project ../Presentation.AgentHub\" \"vite\"",
+    "build": "tsc --noEmit && vite build",
     "lint": "eslint .",
+    "preview": "vite preview",
     "test": "vitest run",
     "test:watch": "vitest",
     "test:coverage": "vitest run --coverage",
-    "preview": "vite preview"
+    "test:ui": "vitest --ui"
   },
   "dependencies": {
     "@azure/msal-browser": "^5.6.3",
diff --git a/src/Content/Presentation/Presentation.WebUI/src/test/vite-proxy.test.ts b/src/Content/Presentation/Presentation.WebUI/src/test/vite-proxy.test.ts
new file mode 100644
index 0000000..e789e7f
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/test/vite-proxy.test.ts
@@ -0,0 +1,18 @@
+import { describe, it, expect } from 'vitest'
+import config from '../../vite.config'
+
+// Verifies that the Vite dev server proxy is configured correctly so that
+// /api/* and /hubs/* requests are forwarded to the AgentHub backend without CORS issues.
+describe('vite dev server proxy config', () => {
+  const proxy = (config as Record<string, unknown> & { server?: { proxy?: Record<string, unknown> } }).server?.proxy
+
+  it('forwards /api requests to localhost:5001', () => {
+    expect(proxy?.['/api']).toBe('http://localhost:5001')
+  })
+
+  it('forwards /hubs requests to localhost:5001 with WebSocket support', () => {
+    const hubs = proxy?.['/hubs'] as { target: string; ws: boolean } | undefined
+    expect(hubs?.target).toBe('http://localhost:5001')
+    expect(hubs?.ws).toBe(true)
+  })
+})
diff --git a/src/Content/Presentation/Presentation.WebUI/vite.config.ts b/src/Content/Presentation/Presentation.WebUI/vite.config.ts
index 7e36ac0..939085a 100644
--- a/src/Content/Presentation/Presentation.WebUI/vite.config.ts
+++ b/src/Content/Presentation/Presentation.WebUI/vite.config.ts
@@ -8,4 +8,13 @@ export default defineConfig({
   resolve: {
     alias: { '@': path.resolve(__dirname, './src') },
   },
+  server: {
+    proxy: {
+      '/api': 'http://localhost:5001',
+      '/hubs': {
+        target: 'http://localhost:5001',
+        ws: true,
+      },
+    },
+  },
 })
