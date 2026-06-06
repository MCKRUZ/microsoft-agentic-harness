# SSRF Defense Library Survey

> Survey date: 2026-06-05. Target: .NET 10 (Microsoft Agentic Harness). Audience: harness engineers wiring `IEgressPolicy` into `HttpClient` for per-skill outbound traffic.

## 1. Executive verdict

**ADOPT `Microsoft.Security.AntiSSRF`** (NuGet id: `Microsoft.Security.AntiSSRF`, repo: `microsoft/AntiSSRF`, license: MIT). It is the only .NET library found that (a) implements the connect-time IP check required to defeat DNS rebinding, (b) re-validates redirect targets, (c) ships a built-in metadata-IP blocklist that includes `169.254.169.254`, (d) is actively maintained on a Microsoft-owned, code-signed release pipeline, and (e) plugs into `HttpClient` via a standard `DelegatingHandler` so it composes cleanly with the harness's existing `IEgressPolicy` decorator. The closest alternative — `idunno.Security.Ssrf` by Barry Dorrans — is technically excellent but is not published to nuget.org (MyGet only at the time of writing). Other "candidates" returned by NuGet search are unrelated. Building from scratch on `IPNetwork2` + `SocketsHttpHandler.ConnectCallback` is feasible but unnecessary given that Microsoft has shipped exactly that pattern, with tests, IPv6 coverage, and a CIDR DSL we would otherwise rebuild.

## 2. Threat model — commitments of the harness HttpClient

The selected library (or hand-rolled equivalent) **must** defend every outbound `HttpClient` request against the following classes of SSRF, in addition to the per-skill hostname allowlist supplied by `IEgressPolicy`:

1. **RFC 1918 private ranges** — `10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`, and IPv6 ULA `fc00::/7` must be rejected unless explicitly allowlisted by the policy.
2. **Link-local** — `169.254.0.0/16` (IPv4) and `fe80::/10` (IPv6).
3. **Loopback** — `127.0.0.0/8` and `::1/128`.
4. **Cloud instance-metadata endpoints** — `169.254.169.254` (AWS/Azure IMDSv1+v2, GCP). Treated as a separate, never-overridable block.
5. **DNS rebinding (TOCTOU)** — hostname → IP must be resolved at the *connect* boundary inside `SocketsHttpHandler.ConnectCallback`, and the resolved IP filtered against the blocklist on every connection attempt. Filtering only at `HttpRequestMessage.RequestUri.Host` is insufficient: an attacker controlling a domain can serve a public IP on the first lookup and a private IP on the second.
6. **Redirect chains** — every `Location` target after a 3xx must be re-validated (URL + resolved IP). Default `HttpClient` auto-redirect must be disabled and replaced with manual redirect handling that funnels each hop through the same filter.
7. **Non-HTTP schemes** — anything other than `http`/`https` (`file://`, `gopher://`, `ftp://`, `dict://`, `ldap://`, etc.) is rejected before resolution.

## 3. Candidate evaluations

### 3.1 `Microsoft.Security.AntiSSRF` (microsoft/AntiSSRF)

| Field | Value |
| --- | --- |
| Maintainer | Microsoft Corporation (org-owned, multiple contributors) |
| License | MIT |
| Repo | https://github.com/microsoft/AntiSSRF |
| NuGet id | `Microsoft.Security.AntiSSRF` (referenced from the org's official "Getting Started" page) |
| Last push | 2026-05-29 (≈ 1 week before survey) |
| Created | 2026-01-07 |
| Stars | 95 |
| .NET targets | .NET Framework + .NET Core (multiple `InnerHandler.*` partials — `NetCore` and `NetStandard`) |
| CVEs | None known |

**DNS rebinding handling — yes.** The `InnerHandler.NetCore` implementation builds a `SocketsHttpHandler` with `ConnectCallback` set to a custom delegate that calls `Dns.GetHostAddressesAsync(ConnectionContext.DnsEndPoint.Host, ct)` *at connect time* and validates each resolved `IPAddress` against the policy's allow/deny CIDR lists before opening the socket. This is the textbook fix for the TOCTOU class of SSRF.

**Allowlist + blocklist — yes.** `AntiSSRFPolicy` exposes `_allowedAddresses` and `_deniedAddresses` as `List<CIDRBlock>`, plus a `DenyAllUnspecifiedIPs` toggle that flips the default-allow stance to default-deny. `IPAddressRanges` is a static catalogue of well-known dangerous ranges, including:

- `privateUse = { "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16" }`
- `loopback = { "127.0.0.0/8", "::1/128" }`
- `linkLocal = { "169.254.0.0/16", "fe80::/10" }`
- `imds = { "169.254.169.254/32" }`
- IPv6 ranges: multicast (`ff00::/8`), benchmarking (`2001:db8::/32`), and others.

**Redirect handling — yes.** `AllowAutoRedirect = false` on the inner handler and a dedicated `RedirectHandler.cs` re-runs the policy on every hop.

**Schemes — yes.** `AllowPlainTextHttp` defaults to `false`, blocking `http://` unless opted in. Non-`http`/`https` schemes are rejected by `URIValidator`.

**Structured logging — partial.** The library throws `AntiSSRFException` on violation; there is no built-in `ILogger` hook. The harness can catch the exception in its `DelegatingHandler` wrapper and emit a structured event.

**Integration shape.** Create an `AntiSSRFPolicy`, hand it to `AntiSSRFHandler` (the public `DelegatingHandler`), and register the handler with `IHttpClientFactory` via `AddHttpMessageHandler`. The handler composes the redirect handler and the SSRF inner handler internally; the harness's own `IEgressPolicy` wrapper sits *outside* it for hostname allowlisting.

### 3.2 `idunno.Security.Ssrf` (blowdart/idunno.Security.Ssrf)

| Field | Value |
| --- | --- |
| Maintainer | Barry Dorrans (long-time .NET security PM, AspNet team alum) |
| License | MIT |
| Repo | https://github.com/blowdart/idunno.Security.Ssrf |
| NuGet distribution | **MyGet only** at the time of survey: `https://www.myget.org/F/blowdart/api/v3/index.json`. The README badges link to nuget.org but the public NuGet feed search for `idunno.Security.Ssrf` returns no result; the package is currently shipped via a personal MyGet feed and a `packageSourceMapping` snippet. |
| Last commit | 2026-05-18 |
| Last release tag | v5.3.0 (`#17`) on 2026-05-09 — explicitly added "TOCTOU warning to `Ssrf.IsUnsafe`", "Validate AllowedHostnames patterns at construction and match time", "Pin validated `RequestUri` before dispatching to inner handler", "ConnectCallback host/port invariant and fix IPv6 proxy detection" |
| Stars | 17 |
| .NET targets | .NET 8, 9, 10 |
| CVEs | None known |

**DNS rebinding handling — yes.** README explicitly calls out the TOCTOU attack and the library is built around `SsrfSocketsHttpHandlerFactory.Create()` which configures `ConnectCallback`-time resolution and validation. Recent commit history shows the maintainer actively hardening the rebinding path (v5.3.0 release notes above).

**Allowlist + blocklist — yes.** Configurable `AllowedHostnames` patterns plus extensible default block list (`Ssrf.cs`). `IsUnsafeUri` / `IsUnsafeIpAddress` helpers exposed for callers who want to check without dispatching.

**Redirect handling — yes** (`ProxiedSsrfDelegatingHandler` validates and re-pins `RequestUri`).

**Schemes — yes** (rejects unknown schemes).

**Integration shape.** `new HttpClient(SsrfSocketsHttpHandlerFactory.Create(connectTimeout: ...))`. Also supports `ClientWebSocket` via the same handler — a feature the harness does not currently need but may want for streaming MCP transports later.

**Why not the verdict.** Strong library, single trusted author, but distribution via MyGet is a hard blocker for a *template* repo whose consumers must `nuget restore` from default feeds with no extra config. We are not going to ask enterprise consumers to add a third-party `packageSourceMapping` to their `nuget.config` for an SSRF dependency.

### 3.3 `IPNetwork2` (lduchosal/ipnetwork)

| Field | Value |
| --- | --- |
| Maintainer | Luc Doschossis (`lduchosal`) |
| License | BSD-2-Clause |
| Last push | 2026-05-27 |
| Stars | 524 |
| Latest NuGet version | 4.3.0 |
| .NET targets | .NET Standard 2.0 (consumable from .NET 8+) |
| CVEs | None known |

**Not an SSRF library.** This is the CIDR / subnet math primitive — pure IP range arithmetic for v4 and v6. It does not understand `HttpClient`, `SocketsHttpHandler`, redirects, or DNS. It would be the foundation for a hand-rolled SSRF handler, paired with `SocketsHttpHandler.ConnectCallback`. Included here only because the survey brief listed it as a candidate.

### 3.4 `SsrfSharp` — **does not exist**

NuGet search for "SsrfSharp" returns no package, and there is no GitHub repository of that name. The closest hit is `idunno.Security.Ssrf` (3.2). Conclusion: the name in the brief is a phantom; do not chase it.

### 3.5 `Microsoft.Identity.Web` SSRF helpers — **does not exist**

`Microsoft.Identity.Web` is the MSAL-for-ASP.NET wrapper. It has no SSRF defenses. The Microsoft SSRF offering is the separate `Microsoft.Security.AntiSSRF` package above.

### 3.6 Polly SSRF community extensions — **none found**

A NuGet and GitHub search for SSRF-themed Polly resilience pipelines returns nothing of substance. Polly's strategies operate above the socket layer; SSRF defense requires the socket layer. This combination is not idiomatic and no community has shipped it.

### 3.7 .NET port of `ssrf-req-filter` / `ssrf_filter` — **none found**

The Node.js (`ssrf-req-filter`) and Ruby (`ssrf_filter`) libraries have no published .NET port. Searches under "ssrf dotnet csharp DelegatingHandler" surface only the two candidates above plus general OWASP cheatsheet content.

## 4. Decision

Adopt **`Microsoft.Security.AntiSSRF`**. It satisfies every must-have (active maintenance, .NET 8+ target, MIT license, `DelegatingHandler` + `ConnectCallback` integration, DNS-rebinding defense) and four of the five preferreds (configurable allowlist + blocklist, public threat-model docs at `microsoft.github.io/AntiSSRF/`, redirect re-validation, default-deny mode via `DenyAllUnspecifiedIPs`). The only preferred it lacks is a structured-logging hook, which the harness will provide by catching `AntiSSRFException` in its own `EgressPolicyHandler` wrapper.

The runner-up `idunno.Security.Ssrf` would be a fine adoption choice for an internal app, but is disqualified for a template by its MyGet-only distribution.

## 5. Integration sketch

The handler chain on a per-skill `HttpClient` is the existing `IEgressPolicy` wrapper *outside*, `AntiSSRFHandler` *inside*, both fed into `IHttpClientFactory`:

```csharp
// Infrastructure.AI.Common/DependencyInjection.cs (illustrative)
services.AddHttpClient("skill-egress")
    .AddHttpMessageHandler(sp =>
    {
        // Outer: per-skill hostname allowlist from the skill manifest.
        var policy = sp.GetRequiredService<IEgressPolicy>();
        var logger = sp.GetRequiredService<ILogger<EgressPolicyHandler>>();
        return new EgressPolicyHandler(policy, logger);
    })
    .AddHttpMessageHandler(sp =>
    {
        // Inner: SSRF defense — RFC1918, IMDS, DNS rebinding, redirects.
        var ssrfPolicy = new AntiSSRFPolicy
        {
            DenyAllUnspecifiedIPs = false,         // start permissive; tune per env
            AllowPlainTextHttp = false,            // https only
        };
        // Default deny list already covers privateUse, loopback, linkLocal, imds, multicast.
        // Add tenant-specific denies here if needed:
        // ssrfPolicy.DeniedAddresses.Add(new CIDRBlock("100.64.0.0/10"));   // CGNAT
        return new AntiSSRFHandler(ssrfPolicy);
    });
```

Order matters. The outer `EgressPolicyHandler` performs the cheap, declarative hostname check (and structured logging on reject) before any DNS resolution; only requests that survive that step descend into `AntiSSRFHandler`, where the connect-time IP check shuts the TOCTOU window. Both must be present — the outer handler alone is rebinding-vulnerable, and the inner handler alone has no notion of skill identity or per-skill allowlists.

Telemetry hook: wrap the `SendAsync` call of `EgressPolicyHandler` with a try/catch on `AntiSSRFException` and emit `gen_ai.egress.ssrf_blocked` with the host, scheme, resolved-IP class (private/link-local/loopback/metadata), and skill id. This converts the library's exception into a first-class signal in the harness's existing OTel pipeline (see `GenAiSemconvRegistry`).

## 6. Open uncertainties

- **NuGet download count for `Microsoft.Security.AntiSSRF` is unverified.** The nuget.org search API call from this environment failed under TLS/network restrictions, so the popularity signal cannot be quoted. The package's existence on nuget.org is referenced from the repo README and from Microsoft's own `microsoft.github.io/AntiSSRF/` documentation, both of which point to `https://www.nuget.org/packages/Microsoft.Security.AntiSSRF/`. Recommend a one-line verification (`dotnet add package Microsoft.Security.AntiSSRF`) when this decision is implemented.
- **`idunno.Security.Ssrf` on nuget.org.** The README badges suggest a NuGet listing exists, but a direct query for the package id returned no public-feed hit during this survey, and the README's "Pre-releases" section explicitly publishes to MyGet. If the maintainer publishes a stable release to nuget.org during the harness lifetime, re-evaluate — its API surface (`SsrfSocketsHttpHandlerFactory.Create()`) is simpler than `Microsoft.Security.AntiSSRF` and would cost less per-call boilerplate.
- **AntiSSRF + `IHttpClientFactory` lifecycle.** The library's `_editLock` semantics throw if policy properties are mutated after the first request. The factory pattern above instantiates a fresh policy per handler-resolution scope, which is safe, but if a future change registers the policy as a singleton and tries to retune it at runtime, it will throw. Document this in the harness's HttpClient factory section.
- **IPv6 ULA (`fc00::/7`) coverage.** Confirmed `IPAddressRanges` includes a wide IPv6 set (link-local `fe80::/10`, multicast `ff00::/8`, benchmarking `2001:db8::/32`, etc.) but the specific `fc00::/7` ULA block was not surfaced by the partial source dump used in this survey. Either confirm via the full `IPAddressRanges.json` source or add it explicitly to `AntiSSRFPolicy.DeniedAddresses`.
- **WAF / forward-proxy interaction.** If the harness's egress traffic is forced through a corporate proxy, the library's `ProxiedSsrfDelegatingHandler` path applies (idunno) or the standard `SocketsHttpHandler` proxy semantics (AntiSSRF). Validate the chosen library cooperates with the proxy before shipping to enterprises that mandate egress proxies.

## Sources

- [microsoft/AntiSSRF](https://github.com/microsoft/AntiSSRF) — repo metadata, source files inspected: `dotnet/src/AntiSSRFHandler.cs`, `dotnet/src/AntiSSRFPolicy.cs`, `dotnet/src/Helpers/InnerHandler.NetCore.cs`, `dotnet/src/Helpers/RedirectHandler.cs`, `dotnet/src/IPAddressRanges.cs`, `dotnet/src/URIValidator.cs`, `dotnet/src/README.md`.
- [Microsoft.Security.AntiSSRF NuGet package](https://www.nuget.org/packages/Microsoft.Security.AntiSSRF/) (referenced by official docs; live availability to be confirmed at adoption time).
- [AntiSSRF official docs](https://microsoft.github.io/AntiSSRF/) and [.NET API documentation](https://microsoft.github.io/AntiSSRF/dotnet-api/).
- [blowdart/idunno.Security.Ssrf](https://github.com/blowdart/idunno.Security.Ssrf) — README, v5.3.0 release notes, MyGet distribution snippet.
- [idunno.dev SSRF docs](https://ssrf.idunno.dev/) (documentation home).
- [IPNetwork2 4.3.0 on NuGet](https://www.nuget.org/packages/IPNetwork2) and [lduchosal/ipnetwork](https://github.com/lduchosal/ipnetwork).
- [OWASP SSRF Prevention Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/Server_Side_Request_Forgery_Prevention_Cheat_Sheet.html) — TOCTOU class explanation and connect-time validation requirement.
- [Doyensec safeurl write-up](https://blog.doyensec.com/2022/12/13/safeurl.html) — DNS rebinding prevention pattern that both libraries above implement.
- [Microsoft Learn — Use IHttpClientFactory](https://learn.microsoft.com/en-us/dotnet/core/extensions/httpclient-factory) — `DelegatingHandler` composition model.
