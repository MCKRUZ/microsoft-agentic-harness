# Owner/Tenant ID Normalization Migration Runbook

> Applies to: any deployment that persisted knowledge-graph data **before** the D3 identity-canonicalization release (PRs #150–158). Audience: DBA / platform operators upgrading a harness instance. Run once, as part of that upgrade.

## 1. Why this migration exists

D3 made owner and tenant identity **canonical** — trimmed and invariant-lowercased — at every write and every comparison. The single source of truth is `Domain.AI.KnowledgeGraph.Scoping.ScopeIdentity.Canonicalize`:

```csharp
public static string? Canonicalize(string? id)
    => string.IsNullOrWhiteSpace(id) ? null : id.Trim().ToLowerInvariant();
```

(`src/Content/Domain/Domain.AI/KnowledgeGraph/Scoping/ScopeIdentity.cs:38`)

New writes are canonical, and every query canonicalizes its lookup parameter before filtering. But rows written **before** D3 may still hold mixed-case or whitespace-padded `owner_id`/`tenant_id` values. A canonical owner-scoped query (`"user-1"`) will not match a legacy value (`"User-1"` or `" user-1 "`), so an **owner-scoped right-to-erasure can silently miss the subject's legacy data**.

In practice owner IDs are Microsoft Entra object-ids — already lowercase GUIDs — so this is an edge-case safety net rather than a mass mutation. It matters most for deployments that seeded owner/tenant values from a source that was not already canonical (custom onboarding, imports, tests promoted to prod). Because it protects erasure completeness, running it is mandatory for any pre-D3 deployment; on already-canonical data it is a verified no-op.

### Canonicalization semantics the scripts must match

1. **Trim** leading/trailing whitespace.
2. **Lowercase** using invariant (culture-independent) casing.
3. **Whitespace-only or empty → `null`.** A `null` owner/tenant denotes a global (unscoped) record, so a value that trims to the empty string must become `null`, not `''`. Every script below maps whitespace-only to `null` via `NULLIF(lower(trim(x)), '')` (SQL) or an explicit `CASE` (Cypher).

### Idempotency

Every script is guarded so re-running it is a **no-op on already-canonical rows** (including rows already `null`). The `WHERE` clause selects only rows whose stored value differs from its canonical form, using a null-safe comparison (`IS DISTINCT FROM` in PostgreSQL, `IS NOT` in SQLite). Safe to run more than once.

### Caveats (edge cases for non-ASCII / exotic whitespace)

These do not affect the common case (GUID or e-mail owner IDs with at most leading/trailing ASCII spaces), but a template consumer should know the boundaries:

- **SQLite `lower()` is ASCII-only.** For a non-ASCII owner ID it will not match .NET `ToLowerInvariant()`. GUIDs and ASCII e-mails are unaffected.
- **`trim()` removes ASCII spaces only** in PostgreSQL and SQLite (not tabs/newlines), whereas .NET `string.Trim()` removes all Unicode whitespace. Owner IDs padded with tabs/newlines are not expected; if yours are, widen the trim character set explicitly.
- **PostgreSQL `lower()` is locale-aware** but agrees with invariant-lowercase for ASCII. Non-ASCII identifiers under a non-C collation could theoretically diverge; again, not a concern for GUID/e-mail IDs.

If your owner/tenant IDs are guaranteed lowercase GUIDs, you can run the verification query (step per backend) first — a count of `0` means there is nothing to migrate and you can skip the `UPDATE`.

## 2. When to run

- **Once**, during the maintenance window in which you upgrade the deployment past the D3 release.
- **After** the new code is deployed (the new code already writes canonical values, so running the migration after cutover fixes the historical tail and nothing re-introduces legacy values).
- Take a backup / snapshot first, per your normal DBA change process. These are in-place `UPDATE`s.

## 3. PostgreSQL — `PostgreSqlGraphStore`

Schema: two tables `kg_nodes` and `kg_edges`, each with `owner_id TEXT` and `tenant_id TEXT`
(`src/Content/Infrastructure/Infrastructure.AI.KnowledgeGraph/PostgreSql/PostgreSqlGraphStore.cs:43-44, 56-57`).
Cross-session memory records are persisted as rows in `kg_nodes` (node `type = 'Memory'`), so they are covered by the same statements.

```sql
-- kg_nodes: owner + tenant
UPDATE kg_nodes SET owner_id  = NULLIF(lower(trim(owner_id)),  '')
  WHERE owner_id  IS DISTINCT FROM NULLIF(lower(trim(owner_id)),  '');
UPDATE kg_nodes SET tenant_id = NULLIF(lower(trim(tenant_id)), '')
  WHERE tenant_id IS DISTINCT FROM NULLIF(lower(trim(tenant_id)), '');

-- kg_edges: owner + tenant
UPDATE kg_edges SET owner_id  = NULLIF(lower(trim(owner_id)),  '')
  WHERE owner_id  IS DISTINCT FROM NULLIF(lower(trim(owner_id)),  '');
UPDATE kg_edges SET tenant_id = NULLIF(lower(trim(tenant_id)), '')
  WHERE tenant_id IS DISTINCT FROM NULLIF(lower(trim(tenant_id)), '');
```

`IS DISTINCT FROM` is null-safe: it treats a whitespace-only `owner_id` (whose canonical form is `null`) as *different* and rewrites it to `null`, and it treats an already-`null` value as *equal* (skipped). A plain `<>` would evaluate to `UNKNOWN` against `null` and silently skip whitespace-only rows.

**Verification** (each must return `0`):

```sql
SELECT count(*) FROM kg_nodes WHERE owner_id  IS DISTINCT FROM NULLIF(lower(trim(owner_id)),  '');
SELECT count(*) FROM kg_nodes WHERE tenant_id IS DISTINCT FROM NULLIF(lower(trim(tenant_id)), '');
SELECT count(*) FROM kg_edges WHERE owner_id  IS DISTINCT FROM NULLIF(lower(trim(owner_id)),  '');
SELECT count(*) FROM kg_edges WHERE tenant_id IS DISTINCT FROM NULLIF(lower(trim(tenant_id)), '');
```

## 4. Neo4j — `Neo4jGraphStore`

Schema: nodes carry the label `:Entity`; relationships are typed `:RELATES`. Both carry `owner_id` and `tenant_id` properties
(`src/Content/Infrastructure/Infrastructure.AI.KnowledgeGraph/Neo4j/Neo4jGraphStore.cs:76-77, 125-126, 449-450, 472-473`).
Memory records synced to a Neo4j backend are `:Entity` nodes (`type = 'Memory'`) and are covered here.

```cypher
// :Entity nodes — owner
MATCH (n:Entity) WHERE n.owner_id IS NOT NULL
SET n.owner_id = CASE WHEN trim(n.owner_id) = '' THEN null ELSE toLower(trim(n.owner_id)) END;

// :Entity nodes — tenant
MATCH (n:Entity) WHERE n.tenant_id IS NOT NULL
SET n.tenant_id = CASE WHEN trim(n.tenant_id) = '' THEN null ELSE toLower(trim(n.tenant_id)) END;

// :RELATES relationships — owner
MATCH ()-[r:RELATES]->() WHERE r.owner_id IS NOT NULL
SET r.owner_id = CASE WHEN trim(r.owner_id) = '' THEN null ELSE toLower(trim(r.owner_id)) END;

// :RELATES relationships — tenant
MATCH ()-[r:RELATES]->() WHERE r.tenant_id IS NOT NULL
SET r.tenant_id = CASE WHEN trim(r.tenant_id) = '' THEN null ELSE toLower(trim(r.tenant_id)) END;
```

The `CASE` maps a whitespace-only value to `null` (matching `ScopeIdentity`); setting a property to `null` in Cypher removes it, which is the canonical "unscoped" state. These statements are idempotent — re-running yields the same value — so the `IS NOT NULL` filter is sufficient; no separate difference guard is required for correctness. For very large graphs, batch with `CALL { ... } IN TRANSACTIONS OF 10000 ROWS` to avoid a single oversized transaction.

**Verification** (each must return `0`):

```cypher
MATCH (n:Entity)        WHERE n.owner_id  IS NOT NULL AND (n.owner_id  <> toLower(trim(n.owner_id))  OR trim(n.owner_id)  = '') RETURN count(n);
MATCH (n:Entity)        WHERE n.tenant_id IS NOT NULL AND (n.tenant_id <> toLower(trim(n.tenant_id)) OR trim(n.tenant_id) = '') RETURN count(n);
MATCH ()-[r:RELATES]->() WHERE r.owner_id  IS NOT NULL AND (r.owner_id  <> toLower(trim(r.owner_id))  OR trim(r.owner_id)  = '') RETURN count(r);
MATCH ()-[r:RELATES]->() WHERE r.tenant_id IS NOT NULL AND (r.tenant_id <> toLower(trim(r.tenant_id)) OR trim(r.tenant_id) = '') RETURN count(r);
```

## 5. Kuzu — `KuzuGraphBackend` (SQLite-backed)

`KuzuGraphBackend` is currently implemented on an embedded **SQLite** database (`Microsoft.Data.Sqlite`); the "Kuzu" name reserves the seam for a future native binding
(`src/Content/Infrastructure/Infrastructure.AI.RAG/GraphRag/KuzuGraphBackend.cs:9-13, 47-63`).
Because it is SQLite, it supports bulk `UPDATE` with `lower()` / `trim()` — **no read-rewrite loop is needed**.

Schema: tables `Nodes` and `Edges`. Both carry `owner_id TEXT` **and no `tenant_id`** — this RAG graph backend is owner-scoped only; tenant isolation lives in the `KnowledgeGraph` backends (PostgreSQL / Neo4j / in-memory)
(`src/Content/Infrastructure/Infrastructure.AI.RAG/GraphRag/KuzuGraphBackend.cs:105-127`).
Cross-session memory records synced here are rows in `Nodes` (`type = 'Memory'`) and are covered.

```sql
-- Nodes: owner (no tenant column on this backend)
UPDATE Nodes SET owner_id = NULLIF(lower(trim(owner_id)), '')
  WHERE owner_id IS NOT NULLIF(lower(trim(owner_id)), '');

-- Edges: owner
UPDATE Edges SET owner_id = NULLIF(lower(trim(owner_id)), '')
  WHERE owner_id IS NOT NULLIF(lower(trim(owner_id)), '');
```

SQLite's `IS NOT` is the null-safe inequality (the SQLite equivalent of PostgreSQL's `IS DISTINCT FROM`): a whitespace-only `owner_id` canonicalizes to `null` and is rewritten; an already-`null` row is skipped. See the SQLite/ASCII caveats in §1.

**Verification** (each must return `0`):

```sql
SELECT count(*) FROM Nodes WHERE owner_id IS NOT NULLIF(lower(trim(owner_id)), '');
SELECT count(*) FROM Edges WHERE owner_id IS NOT NULLIF(lower(trim(owner_id)), '');
```

If the deployment later swaps in a native Kuzu binding whose Cypher dialect lacks a bulk property `UPDATE`, fall back to a read-rewrite: select every node/edge with a non-canonical `owner_id`, recompute the canonical value in application code (call `ScopeIdentity.Canonicalize`), and re-persist. The SQLite implementation shipped today does not need this.

## 6. In-memory backend — N/A

`InMemoryGraphStore` and the `CrossSessionMemoryStore` cache hold no durable state — they are rebuilt from empty on every process start, and every write already runs through `ScopeIdentity.Canonicalize`
(`src/Content/Infrastructure/Infrastructure.AI.RAG/GraphRag/CrossSessionMemoryStore.cs:87`).
There is nothing to migrate.

## 7. Post-migration checklist

- [ ] Backup/snapshot taken before running.
- [ ] `UPDATE` statements run for every persistent backend in use.
- [ ] All verification queries return `0`.
- [ ] Spot-check one previously-legacy owner: run an owner-scoped erasure or recall with the canonical (lowercase, trimmed) ID and confirm the historical rows are now matched.
