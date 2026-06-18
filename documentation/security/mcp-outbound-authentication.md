# MCP Outbound Authentication

> Date: 2026-06-18. Target: .NET 10 (Microsoft Agentic Harness). Audience: harness engineers and template consumers configuring how the harness authenticates to **external** MCP servers it consumes as a client.

## 1. Executive summary

When the harness acts as an MCP **client** — connecting out to a third-party tool server — it authenticates with **its own workload identity**, never by forwarding a caller's token. The recommended, secure-by-default credential is **managed identity**: the harness mints a short-lived, auto-rotating access token per request, so there is no standing secret on the wire and nothing to rotate by hand. Static API keys and bearer tokens remain supported for servers that require them, and a client secret or certificate is available as an explicit Entra fallback. A server whose auth block is configured but incomplete now **fails the connection loudly** instead of silently connecting with no credential.

This is the client-side counterpart to [`ssrf-defense.md`](./ssrf-defense.md): every outbound MCP request passes through both the SSRF guard (connect-time IP filtering) **and**, for Entra, the token injector.

## 2. Why no token passthrough

The MCP security guidance forbids a client from forwarding the end user's credential to a downstream server. The harness satisfies this **by construction**: outbound auth is built only from the server's own `McpServerAuthConfig` (admin-configured), never from the inbound caller's `ClaimsPrincipal`. There is no code path on the outbound transport that reads the user's token. The improvement in this phase is not closing a passthrough hole (there was none) — it is making the harness's *own* credential rotating and secret-free by default, and refusing to connect when auth is half-configured.

## 3. The four auth types

Configured per server under `AI:McpServers:Servers:<name>:Auth` (`McpServerAuthConfig`):

| Type | What goes on the wire | When to use |
| --- | --- | --- |
| `None` | No auth header | Local stdio servers, trusted unauthenticated endpoints |
| `ApiKey` | `<ApiKeyHeader>: <ApiKey>` (default header `X-API-Key`) | Servers that gate on a static API key |
| `Bearer` | `Authorization: Bearer <BearerToken>` | Servers expecting a static, externally-issued token |
| `Entra` | `Authorization: Bearer <minted-token>`, refreshed per request | **Preferred.** Microsoft Entra–protected servers |

`ApiKey` and `Bearer` carry a **static** secret from configuration. They work, but the secret is long-lived and must be rotated manually; persist it via user-secrets (development) or Key Vault (production), never `appsettings.json`.

## 4. Entra credential shapes

For `Type = Entra`, the harness selects one of three shapes from the config fields (via `AzureCredentialFactory`). **`Scopes` is required in all three** — it names the resource the token is minted for (typically `api://<app-id>/.default`). Without a scope there is no resource to request a token for, so the config is treated as invalid.

| Shape | Fields set | Notes |
| --- | --- | --- |
| **Managed identity** (preferred) | `Scopes` only — or `Scopes` + `ClientId` for a user-assigned identity | No stored secret. Azure rotates the credential; the harness never sees it. Best on App Service, Container Apps, AKS, or a VM. |
| Client secret | `TenantId` + `ClientId` + `ClientSecret` + `Scopes` | Explicit fallback. Long-lived secret — rotate on a tight schedule, store in Key Vault. |
| Certificate | `TenantId` + `ClientId` + `CertificatePath` + `Scopes` | Explicit fallback. Stronger than a secret, still operator-managed. |

When neither `ClientSecret` nor `CertificatePath` is set, the harness falls back to `DefaultAzureCredential` (managed identity, then developer credentials such as Visual Studio / Azure CLI), so the same config works in production and on a developer machine.

> **Dev-machine caveat.** `DefaultAzureCredential` walks the full credential chain. On a developer's machine that means a managed-identity-shape Entra server mints a token from **whatever identity the developer is signed into** (`az login` / Visual Studio account) and sends it to the configured server URL. The SSRF guard blocks internal addresses, but an admin-configured *external* URL is allowed — so a developer's personal Entra token can reach a third-party MCP server. For production, prefer a pinned `ManagedIdentityCredential` (set `ClientId` for a user-assigned identity) over the ambient chain, and only point Entra servers at endpoints you trust with the harness's identity. A lone `TenantId` with no secret or certificate is rejected as a half-configured fallback, so a forgotten credential fails loudly instead of silently using the ambient identity.

### Example — managed identity (recommended)

```json
"Auth": {
  "Type": "Entra",
  "Scopes": [ "api://contoso-tools-server/.default" ]
}
```

### Example — client-secret fallback

```json
"Auth": {
  "Type": "Entra",
  "TenantId": "<tenant-guid>",
  "ClientId": "<app-guid>",
  "ClientSecret": "<from Key Vault / user-secrets>",
  "Scopes": [ "api://contoso-tools-server/.default" ]
}
```

## 5. How rotation works

For an Entra server, the connection uses a per-server `HttpClient` whose handler chain is:

```
EntraTokenAuthHandler  →  AntiSSRF handler (shared, connect-time IP filter)  →  socket
```

`EntraTokenAuthHandler.SendAsync` calls `TokenCredential.GetTokenAsync` on **every** request. Azure.Identity caches the token in-process and refreshes it shortly before expiry, so:

- the per-request call is near-instant on the hot path (served from cache), and
- a long-lived, cached MCP connection never sends an expired token — the next request after expiry transparently mints a fresh one.

There is no static `Authorization` header captured once at connection time, which is what previously made a cached Entra connection impossible to keep authenticated.

The token handler sits **in front of** the shared SSRF guard, so Entra traffic still receives the full connect-time IP filtering (RFC 1918 / loopback / link-local / IMDS / IPv6 ULA) and redirect re-validation described in `ssrf-defense.md`.

## 6. Fail-loud on incomplete configuration

Previously, an auth block that was selected but incomplete (e.g. `Type = Entra` with no scope, or `Type = Bearer` with an empty token) silently connected **with no credential**. That class of misconfiguration now throws `McpConnectionException` at connection time with a message naming the server and instructing the operator to complete the fields or set `Type = None`. Validity is defined by `McpServerAuthConfig.IsValid`; the connection manager rejects any server that is configured-but-invalid rather than degrading to anonymous access.

## 7. Implementation map

| Concern | Type | Project |
| --- | --- | --- |
| Auth config + validity rules | `McpServerAuthConfig`, `McpServerAuthType` | `Domain.Common` |
| Credential shape selection | `AzureCredentialFactory` | `Application.Common` |
| Per-request token injection (rotation) | `EntraTokenAuthHandler` | `Infrastructure.AI` |
| Transport wiring, client selection, fail-loud | `McpConnectionManager` | `Infrastructure.AI.MCP` |
| SSRF guard composed under every client | `AntiSsrfHandlerFactory` | `Infrastructure.AI` |
