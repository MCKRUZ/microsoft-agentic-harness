# Plan: Tamper-Evident Audit Chain

**Status:** PR 1 ✅ merged (#69). PR 2 ✅ merged (#70). PR 3 ✅ investigated → **not needed** (see below).
**Date:** 2026-06-20

## Progress log
- **PR 1 (merged #69):** `HashChainedJsonlWriter` primitive + `AuditChainVerificationResult`;
  migrated the two zero-integrity writers (change, egress). Trusted head recovery, framing-char
  rejection, brownfield tolerance.
- **PR 2 (this branch):** Generalized the primitive to **segmented (multi-file) chains**; migrated
  drift (date-partitioned, cross-file chain) and escalation (single-file). Added
  `IVerifiableAuditChain` on all four writers, the `AuditChainVerificationService` background
  verifier (metrics + critical-log + JSONL receipts), `AuditConfig`, and DI wiring.
  - **Design decision:** drift now partitions by **append time**, not `RecordedAt`, so the global
    chain sequence stays monotonic across day files. `GetRecordsAsync` compensates with a precise
    `RecordedAt` post-filter + a ±1-day file-window widening, so date-range queries are unchanged
    for callers.
  - **Verifier hardening:** interval floored to 1 min (no hot loop), loop body survives any
    non-cancellation fault (an audit verifier must never `StopHost`).

---
**Source:** Gap analysis vs "AI Gateway Blueprint: Five Governance Functions" (Wasowski, 2026)
**Related memory:** MCP Hardening (`project_mcp_hardening.md`), Multi-tenant isolation (`project_maf_native_adoption.md`)

---

## Why this exists (plain language)

A court has already held a company liable for what its chatbot said. The regulatory
defense against that is a log you can *prove* nobody altered after the fact — the "black
box." The harness has an audit trail, but today it can only catch **accidental**
corruption, not **deliberate** tampering. This plan closes that gap and nothing else.

The article's other four functions (PII redaction, per-identity cost, virtual keys,
observability) are either already present in the harness or deliberately deferred. The
audit log is the **one genuine, on-brand gap** for a compliance-focused template.

---

## Current state (verified by reading the code, 2026-06-20)

There is no single audit log. There are **four** append-only JSONL writers, with
**inconsistent** integrity guarantees:

| Writer | File | Integrity today |
|---|---|---|
| `JsonlDriftAuditStore` | `Infrastructure.AI/DriftDetection/` | Per-record SHA-256 (tab-appended), verified on read |
| `JsonlEscalationAuditStore` | `Infrastructure.AI/Escalation/` | Per-record SHA-256, verified on read |
| `JsonlChangeAuditWriter` | `Infrastructure.AI/Changes/` | **None** — raw JSON line, no hash |
| `JsonlEgressAuditWriter` | `Infrastructure.AI/Egress/` | **None** (confirm during impl) |

**Three problems, all confirmed in source:**

1. **Hashes are standalone, not chained.** `JsonlDriftAuditStore.ComputeIntegrityHash`
   (line 220) hashes *that record's JSON only*. An attacker who edits a record simply
   recomputes its hash — the tamper is invisible. A chain (each record hashes the
   previous one) makes any edit cascade-break every record after it.

2. **No scheduled verification.** `VerifyIntegrity` runs only when a record is *read*.
   The article's exact warning: without a periodic job that walks the whole chain,
   "the cryptography exists only on paper."

3. **No content/erasure story.** The harness already has erasure plumbing for the
   knowledge graph (`DefaultErasureOrchestrator`, `ComplianceAwareGraphStore`,
   `ErasureReceipt`) — but it is **not** wired to the audit logs. A WORM audit log that
   contains prompts/responses collides head-on with the right to be forgotten.

---

## Design

### Principle: build the primitive once, apply it everywhere

The harness convention (per memory: "shared SQLite helpers extracted at N=2") is to
factor shared behavior rather than copy-paste. We have N=4 writers. So: build **one**
hash-chain primitive and route all four through it, replacing the two ad-hoc hash
implementations and adding integrity to the two that have none.

### Tier 1 — Hash-chain + nightly verifier (all four writers)

These writers store **decisions, identity, and metadata** (gate actions, drift scores,
escalation events, egress allow/deny) — not raw prompts. They need integrity, not
erasure. Tier 1 covers them fully.

**Line format** (replaces the current `{json}\t{hash}`):

```
{json}\t{recordHash}\t{prevHash}\t{seq}
```

- `recordHash = SHA-256(canonical(json) + prevHash)`
- `prevHash` of record N is the `recordHash` of record N-1 across the **entire stream**
  (not per-file — otherwise deleting a whole day's `.jsonl` is undetectable).
- `seq` is a monotonic counter; gaps reveal a deleted record even at a file boundary.
- Chain head (`{seq, recordHash}`) persists in a sidecar `.chainhead` file per stream,
  read on first append after startup. The head is an *optimization* — the chain is
  self-validating by full walk, so a tampered head is itself caught by the verifier.

**New shared component:**
- `Domain.AI/Audit/AuditChainRecord.cs` — value object: seq, prevHash, recordHash,
  timestamp, payload-as-string.
- `Application.AI.Common/Interfaces/Audit/IHashChainedAuditWriter.cs` — append +
  `VerifyChainAsync` contract.
- `Infrastructure.AI/Audit/HashChainedJsonlWriter.cs` — the one real implementation;
  owns the SemaphoreSlim, FileShare.ReadWrite, snake_case JSON, sidecar head, and
  cross-file chaining. The four existing writers delegate to it (composition, not
  inheritance) and keep their typed public APIs.

**Nightly verifier:**
- `Infrastructure.AI/Audit/AuditChainVerificationService.cs` — a `BackgroundService`
  (hosted) that, on a configurable cron (default daily), walks each stream end-to-end,
  recomputes the chain, and:
  - emits a metric via the existing `GovernanceMetrics` meter
    (`audit.chain.verified` / `audit.chain.broken`),
  - logs structured `Warning`/`Critical` on break with the first bad seq,
  - writes a signed verification receipt (`audit-verify/{date}.jsonl`).
- Config: `AppConfig.AI.Audit.VerificationCron`, `AppConfig.AI.Audit.Enabled`.

### Tier 2 — Content/hash separation + erasure wiring (content-bearing sink only)

**First task is investigation, not code:** confirm which sink actually persists raw
prompt/response text. Candidates: the agent `IAuditSink` / `StructuredLogAuditSink`
(`Infrastructure.AI/Audit/`) and any future turn-level audit. The four JSONL writers
above store hashes/metadata, so they likely need **no** Tier 2 work. **Do not build
Tier 2 against a sink that holds no PII.**

Where content *is* stored:
- **Chain store** (WORM, never deleted): seq, timestamp, identity, prevHash, recordHash,
  and the **hash of the content** — never the content itself.
- **Content store** (deletable, AES-256, key in Key Vault via existing `KeyVaultConfig`):
  seq → encrypted prompt/response payload.
- Deleting a subject's content removes content-store rows; the chain still verifies
  because it holds only the content *hash*, which is unchanged. This is the article's
  exact resolution of the WORM-vs-erasure conflict.
- Wire `IErasureOrchestrator` to purge content-store rows for a subject and stamp an
  `ErasureReceipt` (type already exists). The chain records the erasure as its own
  appended event — the paper trail shows *that* data was erased, not the data.

---

## Phasing (one PR per phase)

**PR 1 — Tier 1 primitive + migrate the two unhashed writers.**
Build `HashChainedJsonlWriter` + chain record + verifier-less unit tests. Route
`JsonlChangeAuditWriter` and `JsonlEgressAuditWriter` (currently hashless) through it.
Net: integrity where there was none. Lowest risk, immediate value.

**PR 2 — Migrate drift + escalation, add the nightly verifier.**
Replace the two standalone-hash writers' integrity with the chain. Add
`AuditChainVerificationService` hosted service + `GovernanceMetrics` counters + config.
Includes a backfill note: existing un-chained files are treated as chain-genesis
(verifier starts the chain from first record on/after rollout; documented, not silent).

**PR 3 — Tier 2 (conditional on the investigation). → INVESTIGATED, NOT NEEDED.**

The gating investigation (2026-06-20) found **no durable audit log stores raw prompt or
response content**, so the WORM-vs-erasure tension does not exist in the harness's audit
chains and content/hash separation would be solving a non-problem. Evidence:

| Sink | Stores | Raw conversation content? |
|---|---|---|
| `JsonlChangeAuditWriter` | gate decisions, structured diff ops, identity, evidence **hash** | No |
| `JsonlEgressAuditWriter` | allow/deny verdict, host/URL, matched rule, identity | No |
| `JsonlDriftAuditStore` | drift metrics / baseline events | No |
| `JsonlEscalationAuditStore` | tool name + **sanitized-for-display** args, summary, approvers | No |
| `StructuredLogAuditSink` (`IAuditSink`) | action/outcome metadata — **log-only, not durable** | No |
| OTel content capture | prompt/response — but **opt-in, off by default, redacted, traces (not WORM)** | N/A |
| `FileSystemToolResultStore` | tool outputs — **ephemeral session cache, not an audit log** | No |

Conclusion: the chained logs hold exactly the decision/identity/hash records a compliance
audit must **retain and resist erasure** — which the hash-chain now hardens correctly. Raw
conversation content lives only in the opt-in, redacted OTel path, never in a WORM log. The
two concerns are already correctly separated. **PR 3 is dropped.** If a future consumer adds a
durable prompt/response transcript log, revisit this with the content/hash split designed above.

---

## Tests (TDD — failing test first, per `rules/testing.md`)

- `HashChainedJsonlWriter`: append N records → tamper record K → verify fails at exactly
  K (not K-1, not K+1). Delete a record → seq gap detected. Delete a whole day file →
  cross-file prevHash mismatch detected.
- Verifier: clean chain → `audit.chain.verified` metric, no warning. Broken chain →
  `Critical` log + `audit.chain.broken` metric + receipt written.
- Tier 2: erase subject → content rows gone, chain still verifies, `ErasureReceipt`
  stamped, erasure event appended to chain.
- Concurrency: parallel appends under the semaphore keep seq monotonic and chain intact.

---

## Scope discipline (what this plan does NOT do)

- No NER/LLM PII redaction (regex layer is sufficient; out of scope).
- No per-identity cost/virtual-key work (separate, lower-priority gap).
- No new tool-description hashing (belongs to the MCP Hardening track, not here).
- No change to the four writers' public APIs — callers are untouched.

---

## Open decisions for Matt

1. **Scope of PR 1+2:** all four JSONL writers, or only the regulatory-critical
   change-proposal + escalation ones? (Recommendation: all four — consistency, and the
   primitive cost is already paid.)
2. **Tier 2 at all?** Depends on whether any sink stores raw prompts/responses. The
   investigation in PR 3 answers this; if nothing stores content, Tier 2 is dropped and
   we're done after PR 2. (Recommendation: gate it on the finding — don't pre-build.)
3. **Verifier cadence + alerting sink:** daily is the article's default. Where should a
   broken-chain alert go — structured log only, or also escalation/AG-UI notification?
   (Recommendation: log + `GovernanceMetrics` for PR 2; escalation hook as a follow-up.)
