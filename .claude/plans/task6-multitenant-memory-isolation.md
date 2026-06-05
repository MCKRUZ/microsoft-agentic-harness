# Task #6 — Safe Multi-Tenant Cross-Session Memory

**Date:** 2026-06-04
**Goal (user-chosen scope):** Full safe multi-user memory. User A's remembered facts must never surface for User B — across users and especially across tenants. Then `KnowledgeBridge.Enabled` is safe to turn on.

## Verified problem (not just "orphaned decorators")

1. **Decorators orphaned.** `Infrastructure.AI.KnowledgeGraph/DependencyInjection.cs:60` returns the raw backend; `TenantIsolatedGraphStore`/`ComplianceAwareGraphStore` wrap nothing.
2. **`SetScope` has zero callers.** Entry points read `oid` (user) but never `tid` (tenant); identity never reaches `KnowledgeScopeAccessor`.
3. **Decorators don't isolate per-record.** `GraphNode`/`GraphEdge` carry only `OwnerId` (no `TenantId`/`DatasetId`). `TenantIsolatedGraphStore.HasAccess()` compares `scope.TenantId` to `scope.TenantId` — tautological; an all-or-nothing gate, never a filter.
4. **The actual leak is in the memory path.** `RememberAsync` → `Id = "memory:{key}"` (no scope namespace); `RecallAsync`/`SearchGraphAsync` → `GetNodeAsync("memory:{term}")` by bare key, no owner/tenant filter. Shared keys collide across users.
5. **Cross-session persistence is dead.** `FlushToGraphAsync` has no production caller; `RememberAsync` writes only to the scoped `InMemorySessionCache`, discarded at scope end.
6. **Captive-dep bug.** `RetentionEnforcementService` (hosted singleton) injects scoped `IErasureOrchestrator`.

## Architecture

Two isolation layers:
- **Primary — scoped memory path** (closes the real leak; lives in `KnowledgeMemoryService`, which is already Scoped so it injects `IKnowledgeScope` directly — no ambient bridge needed).
- **Defense-in-depth — graph-store decorators** (activated + made real; singleton, resolve scope per-op via `IAmbientRequestScope`, mirroring the #3 memory-recall pattern).

### Identity capture (one writer, two chokepoints)
- New Application interface `IKnowledgeScopeWriter { void SetScope(...) }`, implemented by `KnowledgeScopeAccessor`; registered Scoped pointing at the same instance as `IKnowledgeScope`. (Keeps the mutator off the read-only `IKnowledgeScope`.)
- `ClaimsPrincipalExtensions.GetTenantId()` → `tid` claim + AAD `tenantid` namespace fallback.
- HTTP: `KnowledgeScopeMiddleware` (Presentation) after `UseAuthentication` — resolves `IKnowledgeScopeWriter` from `HttpContext.RequestServices`, sets scope from `User` (oid+tid). Covers controllers + AG-UI minimal API.
- SignalR: `KnowledgeScopeHubFilter : IHubFilter` — before each invocation resolves the writer from `invocationContext.ServiceProvider` and sets scope from `Context.User`.
- Scope mapping: `userId = oid`, `tenantId = tid`, `datasetId = oid` (per-user dataset within a tenant).
- This avoids threading `tenantId` through the orchestrator's 7 methods / the command. Both chokepoints run in the same DI scope the MediatR handler + decorators observe.

### Primary: scope the memory path (`KnowledgeMemoryService`)
- Inject `IKnowledgeScope`.
- `ScopeKey = sanitize($"{TenantId}:{UserId}")`. `RememberAsync`: `Id = "memory:{ScopeKey}:{key}"`, `OwnerId = scope.UserId`.
- `RecallAsync`/`SearchGraphAsync`: build the same `memory:{ScopeKey}:` prefix so a user only ever queries their own namespace.
- Session cache search already isolated (scoped per request), but also filter by `OwnerId == scope.UserId` defensively.
- **Flush path:** flush the session cache to the graph store post-turn (in `KnowledgeExtractionBehavior`, which already runs the post-turn write) so cross-session persistence actually works.

### Defense-in-depth: activate + fix decorators
- DI: default `IKnowledgeGraphStore` = backend → `ComplianceAwareGraphStore` → `TenantIsolatedGraphStore`, all singleton, scope resolved per-op via `IAmbientRequestScope`; null ambient → `SystemScope` fallback (full access, owner `"system"`) for background work (retention/learnings). Conditional on `ComplianceEnabled` / `MultiTenantIsolation`.
- Refactor both decorators: replace captured `IKnowledgeScope` ctor arg with `IAmbientRequestScope` + per-op resolve.
- Fix `TenantIsolatedGraphStore`: real per-record read filtering by `OwnerId` (return only nodes the scope `CanAccessDataset`, or `OwnerId == null` shared); enforce on writes.

### Captive-dep fix
- `RetentionEnforcementService`: inject `IServiceScopeFactory`, create a scope per run, resolve `IErasureOrchestrator` from it.

### Flag
- Keep `KnowledgeBridge.Enabled` default **false** (secure-by-default for a template) but document it is now safe to enable once scope is wired. (Sub-decision below.)

## Phasing (each phase: `dotnet build && dotnet test` green → `/code-review`; `gitnexus_impact` before editing prod symbols; stage exact paths)

- **PR 1 — correctness-critical, closes the leak:** identity capture (writer iface + `GetTenantId` + HTTP middleware + hub filter) + memory-path namespacing/filtering + flush wiring + tests. After this, multi-user memory is safe.
- **PR 2 — defense in depth:** activate/refactor decorators onto ambient scope + real `TenantIsolatedGraphStore` filtering + captive-dep fix + tests.
- **PR 3 — true tenant-level isolation (largest blast radius, optional):** add `TenantId` to `GraphNode`/`GraphEdge` + backends (in-memory/Neo4j/Postgres) + stamping in `ComplianceAwareGraphStore` + tenant-level decorator filter.

## PR 1 — DONE (uncommitted, branch `feat/task6-multitenant-memory-isolation`, 2026-06-05)
Memory path is scope-safe; multi-user memory now safe to enable. Build + full suite green (0 failures/18 assemblies).
- `KnowledgeMemoryService`: injects `IKnowledgeScope`; `RememberAsync` builds `memory:{tenant}:{user}:{key}` ids, stamps `OwnerId=scope.UserId`, **writes through to the durable graph store** (the old path only hit the per-request session cache → persistence was dead); `RecallAsync`/`ForgetAsync` use the same scoped id builder.
- Identity capture: `IKnowledgeScopeWriter` (on `KnowledgeScopeAccessor`), `ClaimsPrincipalExtensions.GetTenantId`/`GetUserIdOrNull`, `KnowledgeScopeInitializer` (shared), `KnowledgeScopeMiddleware` (HTTP, after auth), `KnowledgeScopeHubFilter` (SignalR). Sets **user+tenant only**.
- **Identity is AMBIENT (`AsyncLocal` in `KnowledgeScopeAccessor`)** — code-review caught that the orchestrator (`RunOrchestratedTaskCommandHandler`) and DAG planner (`SubPlanStepExecutor`) run sub-agents in fresh child DI scopes where `SetScope` was never called, and the post-turn write runs on a background `Task.Run` after scope disposal. Ambient identity flows into all of those (mirrors `IAmbientRequestScope`), fixing the leak with **zero edits to orchestration/planner**. Agent/Conversation stay scoped.
- Tests: 3 cross-user/cross-tenant isolation tests + write-through/ownerId; `KnowledgeScopeAccessorTests` (child-scope + background flow); `ClaimsPrincipalExtensionsTests`, `KnowledgeScopeInitializerTests`, `KnowledgeScopeMiddlewareTests`.
- Flag stays `KnowledgeBridge.Enabled=false`; doc updated to "safe to enable."

### Carry into PR 2 (from PR 1 review — F5)
`TenantIsolatedGraphStore.HasAccess()` is **tautological** (`ValidateAccess(scope, scope.TenantId, scope.DatasetId)` → compares scope to itself) AND the decorators are still un-wired (DI line 60 = raw backend). Also `KnowledgeMemoryService.SearchGraphAsync`'s **corpus lookups** (`{term}:entity`, `GetNeighborsAsync`, `GetTripletsAsync`) are unscoped — they read shared ingested-corpus knowledge, NOT conversation-memory facts (those are `memory:` nodes with no edges, unreachable via these paths), so PR 1's conversation-memory isolation holds, but PR 2 must (a) fix the validator to compare against the NODE's tenant/owner, (b) wire+activate the decorators on the ambient scope, (c) decide whether corpus recall is tenant-gated.

## PR 2 — DONE (staged, branch `feat/task6-multitenant-memory-isolation`, 2026-06-05)
Defense-in-depth decorators activated and made real. Build + full suite green (0 failures/18 assemblies; KG tests 160→163). 8 files staged.
- **Decorators refactored off captured scope → `IAmbientRequestScope` per-op resolve** (both are singletons wrapping the singleton backend; null ambient = system/background = full access). `ComplianceAwareGraphStore` + `TenantIsolatedGraphStore`.
- **`TenantIsolatedGraphStore` rewritten** from the tautological all-or-nothing `HasAccess()` gate to **real per-record `OwnerId` filtering**: visible iff `OwnerId is null` (shared corpus) OR caller owns it. Applies to all reads; writes reject foreign-owned nodes/edges; `GetNeighbors` access-checks the traversal seed (closes a topology leak); counts/exists reflect visibility.
- **DI chain wired**: backend → `TenantIsolatedGraphStore` (inner, when `MultiTenantIsolation`) → `ComplianceAwareGraphStore` (outer, when `ComplianceEnabled`). Compliance-outer is deliberate: Tenant reads owner off the raw backend (no Recall-audit side-effect), sees stamped owner on writes, and Compliance only audits visible nodes. `TryAddSingleton<IAmbientRequestScope>` keeps the layer self-sufficient for infra-only DI tests.
- **Captive-dep fixed**: `RetentionEnforcementService` (singleton hosted) now resolves scoped `IErasureOrchestrator` via `IServiceScopeFactory.CreateAsyncScope()` per run.
- **`KnowledgeScopeValidator.CanAccessDataset` fixed**: removed the user-id/tenant-id namespace-conflation branch (`scope.TenantId == ownerId`) — owner-level access is now purely `scope.UserId == ownerId`. Tenant-level deferred to PR 3.
- **Code-review (high) fixes applied**: (F2) **removed Compliance owner auto-stamping** (`OwnerId ?? CurrentUserId`) — it only privatized shared writes (learnings/skills/corpus write null owner on purpose; memory sets owner itself). This preserves the user-approved "corpus stays shared" guarantee regardless of ingestion scope. (F4) GetNeighbors seed guard. (F5) validator namespace fix. Documented: erasure-by-owner must run system-scoped (retention path already does); GetNodeCount/NodeExists full-scan cost (owner-aware backend primitive = future); blocked-delete Forget-audit is near-unreachable (memory ids are scope-namespaced/unguessable).
- Tests: TenantIsolated rewritten for per-record semantics with the **real** validator (own/foreign/shared/system-access, write rejection, delete block, neighbor seed leak); ComplianceAware owner-preservation (not-stamped + explicit-preserved); RetentionEnforcement scope-factory resolution.

### Carry into PR 3 (deferred — true tenant-level isolation)
Add `TenantId` to `GraphNode`/`GraphEdge` + backends (in-memory/Neo4j/Postgres) + stamping in Compliance + tenant-level decorator filter (reactivate `ValidateAccess`, currently reserved/unused in prod). Then corpus can be tenant-gated instead of globally shared. Add owner-aware `GetNodeCountAsync`/`NodeExistsAsync` primitives to `IKnowledgeGraphStore` to remove the full-scan cost.

## Sub-decisions (RESOLVED 2026-06-05)
1. **Flag default:** keep `KnowledgeBridge.Enabled = false` + "safe to enable" docs (template secure-by-default).
2. **PR 3 (TenantId model change):** DEFER. Do PR 1 + PR 2 this effort; PR 3 planned as a deliberate follow-up.
