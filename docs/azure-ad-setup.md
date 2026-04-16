# Azure AD Setup Guide

This guide describes how to configure the two Azure AD app registrations required to run AgentHub and the WebUI together.

## Architecture: Two-App Model

| Registration | Purpose | Config location |
|---|---|---|
| **AgentHub** (API app) | Exposes `access_as_user` scope | `appsettings.Development.json` → `AzureAd` |
| **AgentWebUI** (SPA app) | Requests the API scope | `.env.local` → `VITE_AZURE_*` |

`authConfig.ts` constructs the API scope as:
```
api://{VITE_AZURE_API_CLIENT_ID}/access_as_user
```

`VITE_AZURE_SPA_CLIENT_ID` is the client ID the SPA uses to authenticate itself. `VITE_AZURE_API_CLIENT_ID` is the client ID of the backend. They are **different values**.

---

## Step 1: Register the API App (AgentHub)

1. Go to [Azure Portal → App registrations](https://portal.azure.com/#view/Microsoft_AAD_RegisteredApps) → **New registration**.
2. Name: `AgentHub`, Supported account type: single tenant.
3. Under **Expose an API**:
   - Set the Application ID URI to `api://{clientId}` (Azure proposes this automatically).
   - Add scope: `access_as_user` (Admins and users can consent).
4. Note the **Application (client) ID** and **Directory (tenant) ID** — you need both.
5. (Optional) Under **App roles**, add role `AgentHub.Traces.ReadAll` for users who need the global traces view.

---

## Step 2: Register the SPA App (AgentWebUI)

1. Go to **App registrations** → **New registration**.
2. Name: `AgentWebUI`, Supported account type: single tenant.
3. Under **Authentication**:
   - Add platform: **Single-page application**.
   - Redirect URI: `http://localhost:5173` (Vite dev server default).
4. Under **API permissions**:
   - Add permission → **My APIs** → select `AgentHub`.
   - Choose delegated permission `access_as_user`.
   - Grant admin consent if your tenant requires it.
5. Note the **Application (client) ID** — this is your SPA client ID.

---

## Step 3: Configure AgentHub Backend

Set the AzureAd values via `dotnet user-secrets` (recommended for development):

```bash
cd src/Content/Presentation/Presentation.AgentHub

dotnet user-secrets set "AzureAd:TenantId"  "<your-tenant-id>"
dotnet user-secrets set "AzureAd:ClientId"  "<api-app-client-id>"
dotnet user-secrets set "AzureAd:Audience"  "api://<api-app-client-id>"
```

Or add an `appsettings.Development.json` (gitignored):

```json
{
  "AzureAd": {
    "TenantId": "<your-tenant-id>",
    "ClientId": "<api-app-client-id>",
    "Audience": "api://<api-app-client-id>"
  }
}
```

---

## Step 4: Configure the WebUI

Copy `.env.example` to `.env.local` and fill in the values:

```bash
# bash / Git Bash
cp .env.example .env.local

# PowerShell
Copy-Item .env.example .env.local
```

```
VITE_AZURE_SPA_CLIENT_ID=<spa-app-client-id>
VITE_AZURE_TENANT_ID=<your-tenant-id>
VITE_AZURE_API_CLIENT_ID=<api-app-client-id>
VITE_API_BASE_URL=http://localhost:5001
```

`.env.local` is gitignored — never commit it.

---

## Step 5: Run the Full Stack

From `src/Content/Presentation/Presentation.WebUI`:

```bash
npm run dev:all
```

This starts AgentHub on port 5001 and Vite on port 5173 concurrently with color-coded output.

Open `http://localhost:5173`. MSAL redirects to Azure AD → returns with a token. All `/api/*` and `/hubs/*` calls are proxied by Vite — no CORS configuration needed in development.
