# Presentation.ExecutionApi

The lean, headless ASP.NET Core host that lets an external system **inject and run an agent the harness did not write**. A caller uploads a bundle -- a zip containing an `AGENT.md`, its nested `skills/*/SKILL.md`, and any plugin manifests -- gets back a short-lived handle, and starts runs against it. Each run is a bounded multi-turn conversation executed by an *ephemeral* agent that never enters the host's persistent agent registry and whose skills never enter the global skill pool.

It is a composition root, exactly like `Presentation.AgentHub`: `builder.Services.GetServices()` wires every Application + Infrastructure layer, then `AddExecutionApiServices()` adds this host's own controllers, authentication scheme, and rate limiters. Unlike the agent hub it launches no SPA, registers no HealthChecks UI, and serves **no Swagger endpoint** -- the machine-readable contract is the checked-in spec at `documentation/onboarding/assets/openapi/bundle-api.yaml`.

**Audience note.** This README is for engineers working *on* the host. The integration guide for callers -- quickstart, endpoint semantics, error handling, client recipes -- is Chapter 17 of the developer guide (`documentation/onboarding/17-bundle-api.html`). Keep the two in sync when the contract changes.

## Architecture Context

```
┌──────────────────────────────────────────────────────────────────────────────┐
│  Presentation.ExecutionApi  (Composition Root)                                  │
│                                                                              │
│  Program.cs → GetServices(includeHealthChecksUI: false)                      │
│             → AddExecutionApiServices(configuration)                            │
│             → UseDefaultServiceProvider(ApplyValidationPolicy)  ← all envs   │
│                                                                              │
│  Middleware: SecurityHeaders → GlobalException → [HSTS/HTTPS] → Routing      │
│              → Authentication → Authorization → RateLimiter → Controllers    │
│                                                                              │
│  ┌────────────────────────────────────────────────────────────────────────┐ │
│  │  BundlesController  [Authorize]  /api/bundles                          │ │
│  │                                                                        │ │
│  │  POST   /                        → RegisterBundleCommand               │ │
│  │  POST   /{handle}/runs           → RunBundleCommand   ← envelope bound  │ │
│  │  GET    /{handle}/runs/{jobId}   → GetBundleRunQuery                   │ │
│  │  GET    /{handle}/runs/{jobId}/stream → BundleRunStreamer (SSE)        │ │
│  │  DELETE /{handle}                → DeleteBundleCommand                 │ │
│  └───────────────────────────────┬────────────────────────────────────────┘ │
│  ┌───────────────────────────────┴────────────────────────────────────────┐ │
│  │  ToolsController  [Authorize]  /api/tools        ← read-only discovery │ │
│  │                                                                        │ │
│  │  GET    /                        → IToolCatalog.ListGranted            │ │
│  │  GET    /{name}                  → IToolCatalog.FindGranted (404 both  │ │
│  │                                    for absent AND ungranted)           │ │
│  └───────────────────────────────┬────────────────────────────────────────┘ │
└──────────────────────────────────┼──────────────────────────────────────────┘
                                   │ MediatR (validation + audit behaviors)
                                   ▼
        Application.AI.Common/CQRS/Bundles/{RegisterBundle,RunBundle,GetBundleRun,DeleteBundle}
                                   │
                                   ▼
        Infrastructure.AI/Bundles/  BundleStagingService · BundleRunExecutor
                                    BundleRunBackgroundService · BundleWorkspaceCleanupService
                                    InMemoryBundleHandleStore · InMemoryBundleRunDispatchQueue
                                   │
                                   ▼
                       RunConversationCommand (the ordinary harness turn loop)
                       under an armed CapabilityEnvelope + EphemeralAgentOverlay
```

## Key Concepts

### The capability envelope is the load-bearing boundary

The host executes code it did not write, so a bundle's self-declared `allowed-tools`, MCP references, and autonomy are **requests**. The authoritative **grant** is the `CapabilityEnvelope` resolved by `ICapabilityEnvelopeResolver` from the caller's principal against `AppConfig:AI:BundleExecution:Envelopes`.

Two design points that must not be quietly broken:

- **The envelope is resolved in the controller, at the transport boundary**, from `User` -- not in the handler, and not from the handle. A run therefore executes under the grant of the credential that *invoked* it, so a leaked handle cannot escalate privilege.
- **It is fail-closed at every level.** An unmatched caller gets `Default`; an unconfigured `Default` grants nothing; an unparseable `AutonomyCeiling` degrades to `Restricted` (`ParseCeiling` deliberately rejects numeric and comma-composite values that `Enum.TryParse` would widen).

Resolution precedence is exact `sub`/name-identifier match → least-privilege combination of matching roles (intersection of allowlists, minimum ceiling) → `Default`.

> **Two identities, on purpose.** `BundleCallerIdentity.StableId` (ownership, rate-limit partitioning) prefers `oid`, then object-identifier, then name-identifier, then `sub`. `CapabilityEnvelopeResolver` (grant lookup) reads name-identifier or `sub` only. These differ for most Entra tokens. Ownership wants the most stable identifier; the grant table is keyed the way operators write config. Document this whenever you touch either -- it is the single most likely source of "my `BySubject` grant is ignored" reports.

### Ownership binding, non-disclosing

Every operation is owner-scoped. `RunBundleCommandHandler` and `DeleteBundleCommandHandler` check `IBundleHandleStore.GetOwner`; `GetBundleRunQueryHandler` matches `OwnerId` *and* `Handle` on the run record. A foreign resource is reported exactly as a missing one -- `404` for reads, a successful no-op `204` for delete -- so the API never confirms that someone else's handle exists. The stream endpoint reuses the poll query as its owner-scoped pre-flight before committing the response to an event stream.

### Staging: hostile input, guarded before it is parsed

`BundleStagingService` buffers the archive (bounded by `MaxArchiveBytes`), validates its shape from the central directory, extracts under guards, then -- and only then -- reuses the host's ordinary `AgentMetadataParser` / `SkillMetadataParser` / `IPluginManifestReader`. Guards: entry count, declared *and* actual uncompressed size, compression ratio (above a 1 MiB floor), zip-slip, escaping symlinks, and staging/discovery-root disjointness. Every non-success exit deletes the partial extraction; failure reasons describe the guard, never the payload.

The disjointness check exists because the global registries scan discovery roots recursively and are bundle-unaware: a staging root nested under one would let them publish a bundle's private skills globally.

### Two dispatch modes, one executor

`IBundleRunExecutor` is the single place the security ambients are armed. Both triggers call it, which is what stops them from diverging:

| Mode | `Stream` | Driver | Notes |
|------|----------|--------|-------|
| Background | `false` | `BundleRunBackgroundService` drains `IBundleRunDispatchQueue` | Serial -- one run at a time per host |
| Live SSE | `true` | The caller opening `GET .../stream`, via `BundleRunStreamer` | **Not enqueued.** Reclaimed on `StreamReservationTtl` if never claimed |

`BundleRunExecutor.ExecuteAsync` acquires the handle lease *before* claiming the run `Running` (so a run whose handle expired never carries a bogus start time), then claims via the atomic `IBundleRunJobStore.TryBeginRun` compare-and-set, so a stream racing the dispatcher for the same job yields exactly one winner. It arms `EphemeralAgentOverlayAccessor` (agent + owned skills resolve) and `CapabilityEnvelopeAccessor` (the governor enforces) around a materialised `RunConversationCommand`.

> The envelope rides on `BundleRunRecord` because the background dispatcher runs on a flow detached from the HTTP request -- the ambient published during the request does not reach it. **Without re-arming, the governor sees no envelope and fails open.** Any new run trigger must go through `IBundleRunExecutor`, never call `RunConversationCommand` directly.

### Streaming transport

`BundleRunStreamer` adds only the transport concern: it arms `AgentTurnStreamSink` so token deltas become `TEXT_MESSAGE_CONTENT` frames, and clears it in a `finally` so it can never leak onto a later request on the same thread. `BundleStreamEventWriter` serializes against the `BundleStreamEvent` *base type* so `[JsonPolymorphic]` emits the `type` discriminator -- serializing by runtime type silently drops it. The six event records deliberately duplicate a small subset of AG-UI rather than referencing the dashboard's 25-event vocabulary; if a third host needs them, extract a shared SSE primitive then.

### Fail-closed authentication, own audience

`AddExecutionApiAuthentication` throws at startup on three states: unconfigured without opt-in, half-configured (exactly one of `TenantId`/`ClientId`), and contradictory (`AllowAnonymous` *and* a configured scheme). Configured mode validates issuer, audience (`api://{ClientId}`), lifetime, and signing key with `ClockSkew = TimeSpan.Zero`, plus a fallback authorization policy so an endpoint added without explicit metadata still requires authentication.

Anonymous mode registers `AnonymousAuthenticationHandler` (one synthetic principal, so `[Authorize]` is satisfied) plus `ExecutionApiAnonymousModeStartupWarning`, which logs loudly for the life of the process. That principal carries no subject, so every anonymous run resolves to the fail-closed `Default` envelope -- the door is open, the room is empty.

### Rate limiting: two rate policies and one concurrency policy

Partitioned per caller by stable id, falling back to remote IP (the only distinguishing signal in anonymous mode).

| Policy | Kind | Limit |
|--------|------|-------|
| `bundles` | fixed window | 60 / minute — declared at controller scope, so it governs run, poll, and delete |
| `bundles-register` | fixed window | 10 / minute (staging is expensive) |
| `bundles-stream` | **concurrency** | `MaxConcurrentStreamsPerCaller` (default 4), `QueueLimit = 0` |

The stream policy is concurrency rather than rate on purpose: a streamed run executes inline on its connection for the whole conversation, so a request-rate limiter that counts *starts* cannot bound it.

## Project Structure

```
Presentation.ExecutionApi/
├── Program.cs                       Composition root + middleware pipeline (order is not negotiable)
├── Controllers/
│   ├── BundlesController.cs         The five endpoints; resolves the envelope; maps Result → ProblemDetails
│   ├── WorkflowsController.cs       Workflow submission, runs, progress stream, cancel
│   └── ToolsController.cs           Read-only tool discovery, filtered by the caller's envelope
├── DTOs/
│   ├── BundleApiContracts.cs        Register/Start/Run responses; BundleRunResponse projects the record
│   ├── WorkflowRunContracts.cs      Run start/cancel/status projections
│   └── ToolCatalogContracts.cs      Catalog entry + listing; RiskTier travels as a name, not an ordinal
├── Extensions/
│   └── ExecutionApiServiceCollectionExtensions.cs   Controllers, auth, FormOptions cap, rate limiters
├── Services/
│   ├── BundleCallerIdentity.cs      Stable per-caller id (oid → objectidentifier → nameid → sub)
│   ├── AnonymousAuthenticationHandler.cs
│   └── ExecutionApiAnonymousModeStartupWarning.cs
├── Streaming/
│   ├── BundleRunStreamer.cs         Arms the text sink, calls IBundleRunExecutor, emits lifecycle frames
│   ├── BundleStreamEventWriter.cs   `data: {json}\n\n`, flushed, write-serialized
│   └── BundleStreamEvents.cs        The six AG-UI-shaped event records
├── appsettings.json                 Enables BundleExecution; Auth intentionally empty
└── appsettings.Development.json     Local opt-in (anonymous auth)
```

`BundleRunResponse.FromRecord` is the projection boundary: it drops the capability envelope, the seed messages, and every other execution input. **Do not widen it** -- the poll surface must never echo a run's security context back to the caller.

## Configuration

Everything lives under `AppConfig:AI:BundleExecution` (`Domain.Common/Config/AI/BundleExecution/`), validated by `Application.Core/Validation/BundleExecutionConfigValidator.cs`.

| Key | Default | Notes |
|-----|---------|-------|
| `Enabled` | `false` | Off ⇒ all four handlers return `Result.Forbidden` (`403`) |
| `TempRoot` | `""` → `%TEMP%/agent-bundles` | Must be disjoint from all skill/agent discovery roots |
| `MaxArchiveBytes` | 10 MiB | Also sets `FormOptions.MultipartBodyLengthLimit`, so oversize is rejected before MVC buffers the body |
| `MaxEntryCount` | 2000 | Read from the central directory, before a single byte is extracted |
| `MaxTotalUncompressedBytes` | 50 MiB | Checked against declared *and* actual bytes |
| `MaxCompressionRatio` | 100 | Only above a 1 MiB uncompressed floor |
| `HandleTtl` | 30 min | Sliding |
| `RunRecordTtl` | 30 min | Terminal records only |
| `StreamReservationTtl` | 5 min | Unclaimed streaming reservations; deliberately independent of `RunRecordTtl` |
| `MaxConcurrentStreamsPerCaller` | 4 | A concurrency limiter with `QueueLimit = 0` -- excess connections are rejected, not parked |
| `CleanupInterval` | 60 s | `BundleWorkspaceCleanupService` period; a lease held by a running job blocks its directory's deletion |
| `Envelopes` | fail-closed | `Default` / `BySubject` / `ByRole` |
| `Auth:TenantId`, `Auth:ClientId` | unset | This host's **own** audience -- never shared with AgentHub or the MCP server |
| `Auth:AllowAnonymous` | `false` | Explicit local-dev opt-in; `Environment=Development` alone does not disable auth |

The shipped `appsettings.json` sets `Enabled: true` with an empty `Auth` block, so it boots only in Development — where the shipped `appsettings.Development.json` opts into anonymous auth. Every other environment fails closed at startup until a real scheme is configured.

## How to Run

```bash
# Configure the audience (or rely on the development anonymous opt-in)
dotnet user-secrets --project src/Content/Presentation/Presentation.ExecutionApi \
  set "AppConfig:AI:BundleExecution:Auth:TenantId" "<tenant-guid>"
dotnet user-secrets --project src/Content/Presentation/Presentation.ExecutionApi \
  set "AppConfig:AI:BundleExecution:Auth:ClientId" "<client-guid>"

dotnet run --project src/Content/Presentation/Presentation.ExecutionApi
```

There is no `Properties/launchSettings.json`, so set `ASPNETCORE_URLS` explicitly rather than assuming a port. Outside Development the pipeline adds HSTS and HTTPS redirection. `UseDefaultServiceProvider(ApplyValidationPolicy)` enforces `ValidateScopes` + `ValidateOnBuild` in **all** environments, so a mis-wired handler fails at startup rather than on first dispatch.

## Common Tasks

### Grant a caller some capabilities

Add an entry under `Envelopes:BySubject` keyed on the caller's `sub`/name-identifier claim (**not** `oid`), or under `Envelopes:ByRole` keyed on an app role. Remember that multiple matching roles *intersect*, and that only an `Autonomous` ceiling currently permits tool execution -- `Supervised`/`Restricted` suspend tool use entirely because mid-run approval routing is deferred (documented on `CapabilityEnvelope.AutonomyCeiling`).

### Decide what to put in a caller's `AllowedTools`

`GET /api/tools` answers this from the caller's side, but two things are the operator's job and no
endpoint can decide them for you.

**Never grant these over HTTP.** `dashboard_control` and the `render_*` tools (`render_chart`,
`render_image`, `render_form`, `render_table`) exist to drive an interactive client through
`IClientToolBridge`, which **only AgentHub registers**. Granting them to an HTTP caller is
meaningless at best: there is no client on the other end to render into. `echo_lookup` and
`echo_calculate` are demo fixtures — grant them in a smoke test, never in an environment that
matters.

**Registered is not the same as available.** Those same five tools are registered in *every* host
because tool registration is shared, but in any host without `IClientToolBridge` they cannot be
constructed at all. The catalog resolves tools one key at a time precisely so one such tool cannot
fail the whole listing; it omits them and logs a warning naming the key. If you see that warning,
it is telling you the truth about the host — not about the catalog. (The underlying sloppiness —
hosts registering tools whose dependencies they do not provide — predates the catalog and is
tracked separately; the catalog only made it visible.)

### Add an endpoint

Put the behavior in a MediatR command/query under `Application.AI.Common/CQRS/Bundles/` with a FluentValidation validator, then add a thin action to `BundlesController`: resolve `ResolveCallerId()`, pass `OwnerId`, and map failures through the existing `MapFailure`. Do not surface raw error text on the general-failure path -- handlers log the detail; the wire gets a generic message.

### Make runs durable

Replace `InMemoryBundleHandleStore`, `InMemoryBundleRunJobStore`, and `InMemoryBundleRunDispatchQueue` behind their existing interfaces (`Application.AI.Common/Interfaces/Bundles/`). Preserve two contracts: `TryBeginRun` must stay an atomic compare-and-set (it is what makes claiming exactly-once), and a handle lease must continue to pin the staging directory against the cleanup sweeper for the life of a run.

### Change the wire contract

**This is the canonical list.** Nothing is generated from the controller, so nothing mechanically catches drift -- every one of these describes the contract by hand and must be updated in the same commit:

1. `documentation/onboarding/assets/openapi/bundle-api.yaml` -- the OpenAPI spec (routes, schemas, status codes, examples).
2. `documentation/onboarding/17-bundle-api.html` -- the consumer guide (endpoint table, error table, config table, SSE frame table, quickstart).
3. This README -- the route block in *Architecture Context* and the *Configuration* table above.
4. `CLAUDE.md` -- only if the spec's location or the doc-site page count changes.

`BundleRunStatus` numeric values are anchored by a `Domain.AI` enum test -- add new states at the end only.

## Dependencies

| Reference | Why |
|-----------|-----|
| `Presentation.Common` | `GetServices()` composition + `ApplyValidationPolicy` + security-headers/exception middleware |
| `Application.AI.Common` | The bundle CQRS contracts and `ICapabilityEnvelopeResolver` |
| `Domain.AI` | `BundleRunRecord`, `BundleRunStatus`, `CapabilityEnvelope` |
| `Domain.Common` | `BundleExecutionConfig`, `BundleApiAuthConfig` |
| `Microsoft.AspNetCore.Authentication.JwtBearer` | The Entra scheme |

`InternalsVisibleTo` is granted to `Presentation.ExecutionApi.Tests`.

## Testing

`src/Content/Tests/Presentation.ExecutionApi.Tests/` covers the host end to end:

| File | Covers |
|------|--------|
| `BundlesControllerIntegrationTests.cs` | Full-stack HTTP via `WebApplicationFactory<Program>` -- boot (proves `ValidateOnBuild` passes), 404/400/204 paths, staging-guard rejection |
| `BundleStreamingIntegrationTests.cs` | The SSE endpoint over the real pipeline |
| `BundleRunStreamerTests.cs` | Frame sequencing, terminal-frame exclusivity, sink cleanup |
| `ExecutionApiAuthenticationTests.cs` | The three fail-closed startup guards |
| `BundleCallerIdentityTests.cs` | Claim-precedence for the stable id |

```bash
dotnet test src/Content/Tests/Presentation.ExecutionApi.Tests
```
