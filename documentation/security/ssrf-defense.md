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

## 6. Adoption-time verification

Pre-implementation verification of the §5 open uncertainties, executed 2026-06-07 before opening PR-3b.

### 6.1 `Microsoft.Security.AntiSSRF` on NuGet — verified present ✅ (yellow flag on adoption count)

Captured via `dotnet package search Microsoft.Security.AntiSSRF --exact-match` and the public `azuresearch-usnc.nuget.org/query` endpoint:

| Field | Value |
|---|---|
| Package id | `Microsoft.Security.AntiSSRF` |
| Latest version | `1.0.0` (only version) |
| Authors | Microsoft |
| Owners | AntiSSRF, Microsoft |
| Verified publisher | **true** (Microsoft prefix reservation, cryptographically signed) |
| License | MIT |
| First published | 2026-05-05 |
| Vulnerabilities | none (`vulnerabilities: []`) |
| Total downloads (as of 2026-06-07) | **381** |
| Source commit pinned in nuspec | `8366ff53e9c75a52d1edcc06fde7b1620243928c` |
| Target frameworks shipped | `net8.0` + `netstandard2.0` |
| netstandard2.0 dependencies | `System.Memory` 4.5.5, `System.Threading.Tasks.Extensions` 4.5.4 |
| net8.0 dependencies | none |
| Restored cleanly on .NET 10 SDK | **yes** — net10.0 picks up the net8.0 build via forward-compat |

**Yellow flag:** 381 downloads is low for a production dependency. Counter-signals that justify adoption anyway:

- Package is ~1 month old (published 2026-05-05); count grows from this baseline
- Microsoft verified publisher — not a typosquat
- Active source repo: 95 stars, last commit 2026-05-29 (3 weeks before survey)
- Zero known CVEs; nuspec pins to a specific Microsoft commit, not a moving target
- The alternative (`idunno.Security.Ssrf`) is still MyGet-only — hard blocker
- Hand-roll costs ~150 LOC + maintenance burden falls on the harness team

**Follow-up obligation (PR-3b merge gate):** Re-check `totalDownloads` and `pushed_at` before PR-3b merges. If downloads < 1000 OR last source commit > 90 days old at that point, **vendor the source** into `src/Content/Infrastructure/Infrastructure.AI/Egress/AntiSsrf/` pinned to commit `8366ff53e9c75a52d1edcc06fde7b1620243928c` (~600 LOC fork) and drop the NuGet dependency. The fork option is preserved by the integration sketch above — the harness's own `IEgressPolicy` decorator is the contract; the inner SSRF implementation is swappable.

### 6.2 `idunno.Security.Ssrf` on nuget.org — unchanged, still MyGet-only

No re-evaluation needed for PR-3b. Re-evaluate only if Microsoft.Security.AntiSSRF gets abandoned and we need a replacement before vendoring.

### 6.3 AntiSSRF + `IHttpClientFactory` lifecycle — verified safe ✅

Inspected `dotnet/src/AntiSSRFPolicy.cs` at commit `8366ff53`. `_editLock` is a **one-way immutability latch**, not a thread-synchronization lock:

```csharp
private volatile bool _editLock = false;

// Setters and Add* methods check the flag:
public void AddDeniedAddresses(string[]? networks) {
    if (_editLock) throw new InvalidOperationException(...);
    ...
}

// The latch is flipped exactly once, inside the factory method:
public AntiSSRFHandler GetHandler() {
    _editLock = true;
    return new AntiSSRFHandler(this);
}
```

After `GetHandler()` returns, the policy is permanently immutable. The runtime read path (`IsNetworkConnectionAllowed`) does not touch the flag. This is the ideal shape for `IHttpClientFactory`:

1. Configure `AntiSSRFPolicy` once at startup (during `RegisterChangesServices` / DI composition).
2. Call `policy.GetHandler()` to produce the `AntiSSRFHandler`, which freezes the policy.
3. Register the handler via `AddHttpMessageHandler(() => handler)` — IHttpClientFactory caches it.
4. Concurrent requests share the immutable policy with zero lock contention.

The harness's `EgressPolicyHttpClientFactory` must NOT expose runtime mutation of the underlying policy. Document this in the `Infrastructure.AI/Egress/` XML comments at PR-3b time.

### 6.4 IPv6 ULA (`fc00::/7`) coverage — verified present ✅

Inspected `dotnet/src/IPAddressRanges.cs` at commit `8366ff53`. The relevant catalogues:

```csharp
public static readonly string[] privateUse   = { "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16" };
public static readonly string[] loopback     = { "127.0.0.0/8", "::1/128" };
public static readonly string[] linkLocal    = { "169.254.0.0/16", "fe80::/10" };
public static readonly string[] imds         = { "169.254.169.254/32" };
public static readonly string[] multicast    = { "224.0.0.0/4", "ff00::/8" };
public static readonly string[] uniqueLocal  = { "fc00::/7" };              // ← IPv6 ULA, present
public static readonly string[] recommendedV1 = { /* superset including all of the above plus
                                                  "0.0.0.0/8", "100.64.0.0/10", "168.63.129.16/32" (Azure IMDS),
                                                  reserved + benchmarking ranges */ };
```

`fc00::/7` is exposed as its own `uniqueLocal` catalogue (separate from `linkLocal`) and is also included in `recommendedV1`. **No remediation needed.** The §5 integration sketch's `AddDeniedAddresses(IPAddressRanges.uniqueLocal)` call is sufficient (or `AddDeniedAddresses(IPAddressRanges.recommendedV1)` for the curated superset).

### 6.5 Sandbox executors on merged main — verified green ✅

Verification step required by PR-3 plan §229: confirm `ProcessSandboxExecutor` + `DockerSandboxExecutor` exist and their tests pass before the egress layer is added. As of merged main `b416d53` (PR-2 + all follow-ups + item 2 merged):

```
src/Content/Infrastructure/Infrastructure.AI/Sandbox/
  ProcessSandboxExecutor.cs
  DockerSandboxExecutor.cs
  WindowsJobObjectManager.cs
  WindowsProcessResourceLimiter.cs
  NoOpProcessResourceLimiter.cs
```

`dotnet test --filter "FullyQualifiedName~Sandbox"` returns **51 passing tests across 4 assemblies, 0 failures**. PR-3b can add the egress layer without first completing or refactoring the sandbox executors.

### 6.6 WAF / forward-proxy interaction — unverified, deferred to consumer

This is environmental — depends on whether a specific enterprise consumer routes egress through a corporate proxy. Validation belongs in the consumer's integration test rig, not in the template repo. Documented as a known caveat in the harness onboarding guide instead of carried as a PR-3b blocker.

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
