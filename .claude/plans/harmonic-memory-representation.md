# Harmonic Memory Representation (Memora port)

## Origin
Research project: **Memora** (Microsoft Research, ICML — arXiv 2602.03315, MIT license, Python 3.12).
A "harmonic" agent-memory representation that structurally balances **abstraction** and **specificity**.
Core move: **do not index the raw memory value — index a lightweight scaffolding layer over it.**

Each memory entry is a triple:
- **Memory value** (NOT indexed) — full fact, verbatim, no compression/embedding fuzziness.
- **Primary abstraction** (indexed) — one canonical summary of *what the memory is about*; serves as
  the **update/consolidation key** so evolving info aggregates into one entry instead of fragmenting
  (e.g. "Project Orion Timeline" gets milestones appended, not 30 scattered records).
- **Cue anchors** (indexed, 1–3) — `[Entity] + [Aspect]` phrases (2–4 words); many-to-many hooks that
  form an **implicit memory graph** (shared anchors = edges) with no explicit edge construction.

Write path = two LLM stages: **extraction** (segment → candidate `{abstraction, value}` pairs, typed
factual/episodic/procedural) → **consolidation** (LLM sees top-k similar existing entries, decides
**merge-into-existing vs create-new**), then cue-anchor generation. Read path = query matched **jointly**
against abstractions + cue anchors, then traverses shared-anchor neighbors to return a coherent cluster
+ episodic context.

Two retrievers ship: **Semantic** (LoCoMo 0.849) and **Policy** (0.863, an RL-trained Qwen model, GRPO
-style). Ablation: the policy edge **vanishes without cue anchors**, and semantic alone gets ~98% of the
score. Memora beats Full-Context (0.825), Nemori (0.794), Mem0 (0.653), RAG (0.633), and proves RAG and
KG memory are special cases of its framework.

**Numbers are Microsoft's own paper measurements — directional, not independently verified on our stack.**

## Gap analysis vs. this harness
Confirmed by reading `IKnowledgeMemory`, `MemoryRecord`, `KnowledgeMemoryService`.

**What our cross-session memory does today:**
- `RememberAsync(key, content, entityType)` — caller-supplied `key`; content indexed for **substring
  search** + a graph node. Dedup by **content-hash `Id`** (exact duplicates only).
- `RecallAsync(query)` — **substring** match on session cache first, then **graph-neighborhood traversal**.
- `MemoryRecord` = Content + Source + EMA-decay Weight + AccessCount + Metadata.

**The four real gaps:**
1. **No primary abstraction.** `key` is caller-authored, not an LLM-generated canonical identity →
   evolving topics fragment across keys (exactly what Memora's consolidation prevents). *Foundational.*
2. **No cue anchors.** Zero `[Entity]+[Aspect]` secondary index → recall leans on substring/graph
   neighborhood, missing the multi-perspective entry points that drive Memora's precision.
3. **No consolidation-on-write.** `RememberAsync` just inserts; only exact-hash dedup. No "look at
   similar entries, merge vs create" step.
4. **Recall is substring-first**, not a joint abstraction+cue match with implicit-graph traversal.

**What we already have that Memora lacks (we out-govern it):** multi-tenant + owner isolation, provenance
stamping, the memory **write-gate** (poisoning defense — [[guarding-ai-memory]]), EMA feedback-weighted
decay, `Result<T>` discipline, RRF fusion in `Infrastructure.AI.RAG`. Relationship is complementary:
Memora out-designs us on *representation*; we out-govern it on everything around it.

**Explicitly out of scope:** the RL **Policy retriever** (needs a trained Qwen + GRPO trajectory
collection — Memora's whole `src/memora/rl/`). 1.4 LoCoMo points for that much machinery fails our
effort-to-payoff bar. **Target = the Semantic retriever variant (0.849)**, which maps cleanly onto our
stack.

## The cost tradeoff — exposed as a toggle (per Matt)
Our write path today is a cheap in-memory cache insert. Abstraction + consolidation adds **1–2 LLM calls
per remembered fact**. That is the price of the precision gain and it must be a first-class, off-by-default
switch — never silently on.

`AppConfig:AI:HarmonicMemory` config with a **graduated mode toggle**, mirroring how `DataClassification`
(Off/Audit/Enforce) and WorkMemory master-toggles are done:

| `Mode` | Write behavior | Cost |
|---|---|---|
| `Off` (default) | Legacy path unchanged: caller `key`, substring/graph recall. Zero LLM calls. | none |
| `AbstractOnly` | Generate primary abstraction + cue anchors; **skip** consolidation (always create-new). | 1 LLM call/write |
| `Full` | Abstraction + cue anchors + LLM consolidation (merge vs create). | 1–2 LLM calls/write |

Plus cost-guard knobs under the same section: `MinContentLengthChars` (only abstract facts above a
threshold), `ConsolidationTopK` (how many similar entries the consolidator sees), and `BatchAtSessionFlush`
(defer abstraction to session-flush instead of per-`RememberAsync`, amortizing the LLM cost). With a
`NotConfigured*` abstractor registered, even `Mode != Off` fails fast rather than silently no-op'ing —
our established consumer-supplied-impl pattern.

## Decisions locked (2026-07-06)
1. **Scope:** build **PR1 + PR2 only, then run the eval** before greenlighting the cue-anchor read path.
   *Caveat to hold in mind:* an eval after PR2 measures the **write-side** hypothesis (does abstraction +
   consolidation cut fragmentation and help even naive recall?). Memora's largest gains sit in cue-anchor
   **recall** (PR3), so the post-PR2 eval will *under-measure* the full effect — it's a go/no-go gate on
   the write side, not a verdict on the whole idea.
2. **Provider:** ship `IMemoryAbstractor`/`IMemoryConsolidator` as **`NotConfigured` seams only** — no
   bundled OpenRouter impl. Consumer extension point (like the MIP-File resolver in [[project_purview_dlp]]).
3. **Episodic:** **include episodic capture, reconciled with the shipped `WorkEpisode` subsystem** — they
   share the turn-boundary capture seam + graph store + tenant isolation, but stay **distinct records,
   cross-linked**, NOT merged (see "Episodic reconciliation" below). Episodic segments are captured raw +
   no-LLM in PR2 so the grounding data exists when PR3 is decided.
4. **Retriever:** **Semantic retriever only.** RL Policy retriever is out (needs trained Qwen + GRPO).

## Episodic reconciliation (decision 3, grounded in shipped code)
The Self-Improving Work Memory subsystem is **already merged** (`WorkEpisode`, `WorkEpisodeCaptureBehavior`,
`GraphWorkEpisodeStore`, off by default). `WorkEpisode` and Memora episodic memory are **different records**:

| | `WorkEpisode` (shipped) | Memora episodic memory (new) |
|---|---|---|
| Captures | what the agent **did** (task + outcome + token cost) | what was **said** (raw conversation segments) |
| Response text | **truncated** (`ResponseSummaryMaxChars`) to bound storage | **raw, untruncated** — truncation kills the grounding gain |
| Granularity | 1 record / turn | many topical segments / turn |
| Consumed by | offline lesson synthesis → `Learnings` | retrieval-time grounding for factual harmonic entries |

Merging schemas is lossy both ways. Reconciliation:
- **Share the seam, not the schema.** Factor the existing turn-boundary hook (`WorkEpisodeCaptureBehavior`'s
  fire-and-forget + fresh-scope + ambient-scope re-establishment pattern) so **one** turn-end interception
  emits both: the truncated `WorkEpisode` (for synthesis) **and**, when harmonic `Mode != Off`, the raw
  episodic segment(s) (for grounding). No duplicate capture machinery.
- **Cross-link by `(ConversationId, TurnNumber)`.** A `WorkEpisode` and its episodic segment(s) reference
  each other by id — synthesis can reach raw grounding, harmonic recall can reach the outcome signal —
  without collapsing two schemas into a lossy union.
- **Capture stays no-LLM** (consistent with both subsystems): episodic = **raw** segments (Memora's best
  variant uses raw, not LLM-extracted, episodes). Only *factual* entries pay the abstraction/cue-anchor
  LLM cost.

## Plan — PR1 + PR2 now (eval gate), PR3 + PR4 deferred to post-eval

### PR1 — Representation + seams (foundation) — `feat/harmonic-memory-pr1-representation`
Add the data model and the consumer-supplied seams. **No behavior change** — everything gated `Off`.
- **Domain** (`Domain.AI/KnowledgeGraph/Models/`): extend `MemoryRecord` with **optional** `Abstraction`
  (string?) and `CueAnchors` (`IReadOnlyList<string>`, default empty). Add `HarmonicMemoryMode` enum
  (Off/AbstractOnly/Full) and a `MemoryConsolidationDecision` value object (`Action` Merge|Create +
  target `Id?`). Nullable so existing records + substring path are untouched.
- **Domain.Common/Config/AI**: `HarmonicMemoryConfig` (Mode, MinContentLengthChars, ConsolidationTopK,
  BatchAtSessionFlush) under `AppConfig:AI:HarmonicMemory`. Master toggle **off-by-default**.
- **Application** (`Application.AI.Common/Interfaces/KnowledgeGraph/`):
  - `IMemoryAbstractor` — `content → {abstraction, cueAnchors}` (one LLM call). Ships
    `NotConfiguredMemoryAbstractor` (throws on first use — mirrors `NotConfiguredPatchProposer`).
  - `IMemoryConsolidator` — `(candidate, topKSimilar) → MemoryConsolidationDecision`. Ships
    `NotConfiguredMemoryConsolidator`.
- **FluentValidation**: `HarmonicMemoryConfigValidator` (TopK ≥ 1, MinLength ≥ 0; if Mode != Off in
  config but no real abstractor registered, surface a startup diagnostic — see [[assess-before-skipping]]
  "design what's missing": wire the validation, don't just add the option).
- **Tests**: MemoryRecord defaults/immutability, config defaults (Mode == Off), NotConfigured throws,
  validator rules.
- **Deliberately NOT in PR1** (YAGNI): the cue-anchor index and recall changes (PR3), any LLM impl (PR2).

### PR2 — Abstractor + consolidation write path — depends on PR1 — `feat/harmonic-memory-pr2-write`
Make `RememberAsync` produce harmonic entries when `Mode != Off`.
- **Infrastructure** (`Infrastructure.AI.KnowledgeGraph/Memory/`): `LlmMemoryAbstractor` (keyed,
  `IChatClientFactory` via `AppConfig.AI.AgentFramework` — never hardcode model), `LlmMemoryConsolidator`.
  Prompts ported from Memora Appendix A (factual-extraction + cue-anchor + consolidation prompts),
  adapted to our style. LLM output treated as untrusted → sanitize per `rules/security.md` AI/LLM section.
- **`KnowledgeMemoryService.RememberAsync`** (run `gitnexus_impact` first — this is on the live memory
  path): when `Mode != Off` and `content.Length ≥ MinContentLengthChars` →
  1. abstract → `{abstraction, cueAnchors}`
  2. `Mode == Full`: recall top-k similar **by abstraction**, consolidate → Merge appends to the target
     record's `history` (our versioning already supports it) / Create makes a new entry.
  3. **THEN** run the existing write-gate ([[guarding-ai-memory]]).
  **Order is load-bearing: consolidation BEFORE the gate**, so the poisoning defense adjudicates the final
  merged content, not the pre-merge candidate.
- `BatchAtSessionFlush == true`: defer steps to `FlushToGraphAsync` (amortize LLM cost across the session).
- **Episodic capture (raw, no-LLM) + WorkEpisode reconciliation** (decision 3): add episodic segment records
  (new memory_type in the harmonic store; factual entries link via `EpisodicMemoryIds`, mirroring Memora's
  `MemoryEntry.episodic_memory_ids`). Factor the shipped `WorkEpisodeCaptureBehavior` turn-boundary hook so
  one interception emits both the truncated `WorkEpisode` and (when `Mode != Off`) the raw episodic
  segment(s); cross-link by `(ConversationId, TurnNumber)`. Segments stored raw/untruncated.
- **Tests**: AbstractOnly vs Full behavior, MinContentLength skip, merge-appends-history, gate still fires
  post-consolidation, tenant/owner isolation preserved through the new path, cost-guard knobs honored,
  episodic segment captured raw + cross-linked to WorkEpisode without double-capturing the seam.

### 🔬 EVAL GATE — run PR4 eval here before building PR3
Per decision 1: after PR2, run the write-side eval (below) and get Matt's go/no-go before the read path.

### PR3 — Cue-anchor recall + RRF fusion — **DEFERRED to post-eval** — `feat/harmonic-memory-pr3-recall`
Add the joint abstraction+cue read path; keep the legacy path as one fusion input.
- **Infrastructure**: a cue-anchor index (second collection / graph label). `RecallAsync` (impact-analyze
  first) becomes: match query against **abstraction index + cue-anchor index** → traverse shared-anchor
  neighbors (bounded fan-out) → fuse with the existing substring/graph results via **RRF (reuse
  `Infrastructure.AI.RAG` — we already own the primitive)**. Behind `Mode != Off`; `Off` = today's path.
- **Tests**: cue-anchor hit surfaces a memory that substring misses; traversal returns the coherent
  cluster; RRF ordering; isolation holds on recall; `Off` mode = byte-identical legacy behavior.

### PR4 — Eval harness (the gate) — runs after PR2, BEFORE PR3 — `feat/harmonic-memory-pr4-eval`
Small LoCoMo-style eval (reuse our Phase 5 eval framework + LLM-as-judge) comparing Off vs AbstractOnly vs
Full on a fixed multi-session fixture. Report retrieval relevance + answer score + **LLM-call cost delta**
(the toggle's whole justification). Purpose: confirm the gain transfers before recommending anyone flip it
on in production — benchmark numbers rarely port 1:1.

## Verification (each PR)
`dotnet build src/AgenticHarness.slnx && dotnet test src/AgenticHarness.slnx`; `gitnexus_impact` before
editing `RememberAsync`/`RecallAsync`; `/code-review` + `/simplify` per `rules/review-cadence.md`;
`gitnexus_detect_changes` before commit.

## Decisions — all resolved 2026-07-06 (see "Decisions locked" above)
1. Scope → **PR1 + PR2, then eval gate.** 2. Provider → **`NotConfigured` seams only.**
3. Episodic → **include + reconcile with shipped `WorkEpisode` (share seam, distinct records, cross-link).**
4. Retriever → **Semantic only.**

Build not yet started — awaiting Matt's go to begin PR1.
