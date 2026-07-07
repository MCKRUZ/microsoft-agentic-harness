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

### 🔬 EVAL GATE — ✅ DONE (2026-07-07, paid `--llm` run, Matt reviewed)
Write-side eval built + MERGED (#115); judge-wiring fix MERGED (#116). Paid run ⇒ **write side ACCEPTED,
PR3 GREENLIT.** Key finding that reshapes PR3: of the residual Full-mode fragments, **5/6 shared ≥1 token
with their cluster** (surfaceable by *lexical* abstraction match) and only **1/6** was a pure-semantic miss.
⇒ semantic/embedding recall has ~0 marginal leverage on this workload (**Option A abandoned**); the recall
win comes from *using* the abstraction + cue-anchor data at all, not from embeddings. See the "EVAL GATE
RUN" section in `memory/project_harmonic_memory_memora.md` for the full scorecard.

---

## PR3 — Cue-anchor recall + RRF fusion — `feat/harmonic-memory-pr3-recall`  *(drafted 2026-07-07, not built)*

**The problem PR3 solves.** PR2 writes a primary abstraction + cue anchors into every trusted memory node's
`GraphNode.Properties` (`memory.abstraction`, `memory.cue_anchors`). **Recall never reads them.** Today
`RecallAsync` → `SearchGraphAsync` → `MatchesQuery` substring-matches only `node.Name`/`node.Type`
(`KnowledgeMemoryService.cs:332`). That is exactly why the pre-PR3 eval showed **zero recall delta across
modes** — the harmonic write side is inert until recall consumes it. PR3 makes recall match the query against
abstraction + cue anchors, traverse the implicit shared-anchor graph, and fuse that with the legacy path.

**Corrections to the old stub (from the 2026-07-07 code map):**
- There is **no reusable RRF** — the only impl is `private static HybridRetriever.ApplyReciprocalRankFusion`
  (`Infrastructure.AI.RAG/Retrieval/HybridRetriever.cs:142`), coupled to `RetrievalResult`/`DocumentChunk`.
  PR3 must **extract a generic helper**, not "reuse `Infrastructure.AI.RAG`" (and infra-to-infra dep is wrong
  anyway).
- Abstractions live in `GraphNode.Properties` (a string bag), **not** a separate index/collection.
- `RecallAsync(string query, int maxResults)` takes a **bare string** — no query object, no embedder wired.
- No property-filtered graph query exists; PR2 already accepts an **O(n) `GetAllNodesAsync` scan** (documented
  caveat). PR3 recall inherits that tradeoff.

### Recommended design — lexical joint match, no LLM on the recall hot path
Reuse the write partial's `Tokenize`/`TokenOverlap` (`KnowledgeMemoryService.Harmonic.cs:213/229`) to score
the **raw query** against each in-scope trusted node's abstraction + cue anchors. This is the honest default
because (a) the eval proved lexical catches 5/6 of the fragments semantic would and (b) recall runs per-turn
during context assembly — paying an LLM call per recall (query-abstraction) or standing up an embedder
(semantic) buys ≤1/6 for real cost. See **Decision R1** for the fork; recommendation = lexical.

### Stages
1. **Generic RRF helper** — new `ReciprocalRankFusion` static (proposed `Domain.AI/Retrieval/`, pure algorithm,
   zero deps): `Fuse<T>(IEnumerable<IReadOnlyList<T>> rankedLists, Func<T,string> keySelector, double k=60,
   int topK)`. **Migrate `HybridRetriever` to call it** (replace the private method — "replace, don't
   deprecate"; one true RRF, its existing RAG tests prove equivalence). Risk: touches RAG tests → verify green.
2. **Harmonic recall matcher** — new partial `KnowledgeMemoryService.Harmonic.Recall.cs` mirroring the write
   partial. `RecallHarmonicAsync(query, maxResults)`: scope-filtered trusted nodes with an abstraction (same
   filter as `FindConsolidationCandidatesAsync`) → score by `max(TokenOverlap(qTokens, abstractionTokens),
   TokenOverlap(qTokens, cueAnchorTokens))` → ranked list. Cue anchors are the second entry point the legacy
   substring path lacks.
3. **Shared-cue-anchor traversal** — from the top seeds' cue anchors, pull in-scope trusted nodes sharing ≥1
   anchor (Memora's implicit graph), bounded by `RecallCueAnchorFanout` (new knob). These join the fusion at a
   lower rank so a query that hits one member surfaces the coherent cluster.
4. **Fuse in `RecallAsync`** — when `Mode != Off`: build (a) the harmonic ranked list (stages 2–3) and (b) the
   existing `SearchGraphAsync` list, fuse via the generic RRF, keep `IsRecallable` as the single trust
   chokepoint, `Take(maxResults)`. **`Mode == Off` = byte-identical legacy path** (guard at the top).
5. **Config + validator** — extend `HarmonicMemoryConfig`: `RecallCueAnchorFanout` (default 3), `RecallRrfK`
   (default 60). Extend `HarmonicMemoryConfigValidator` (fanout ≥ 0, k > 0). Optional `AbstractQueryOnRecall`
   (default false) only if Decision R1 picks the LLM path.
6. **DI** — inject `IMemoryAbstractor` into the `KnowledgeMemoryService` factory
   (`Infrastructure.AI.KnowledgeGraph/DependencyInjection.cs:164`) **only if** R1 = LLM-query-abstraction;
   the lexical default needs no new dependency (the constructor already has the abstractor arg, currently
   null from the factory).
7. **Tests** (`Infrastructure.AI.KnowledgeGraph.Tests/Memory/`, real `InMemoryGraphStore` + Fake seams, per
   PR2 pattern): cue-anchor hit surfaces a node substring misses; shared-anchor traversal returns the cluster;
   RRF ordering; **`Off` byte-identical to legacy**; tenant/owner isolation holds on recall; quarantined
   (untrusted) nodes never surface. Plus generic-RRF unit tests + HybridRetriever regression stays green.

### 🔬 Recall eval (proves PR3, mirrors the PR2 write eval)
The write eval explicitly **under-measured** because it couldn't test recall. Add a `harmonic-recall` eval
subcommand (template: `Presentation.EvalRunner/HarmonicWriteEval/`) over a fixture of `(query → expected
memory-id)` pairs, reporting **recall@k / MRR for Off vs AbstractOnly vs Full** + LLM-call cost delta. This is
the real go/no-go on whether harmonic recall beats substring — ship it with PR3.

### Open decisions for PR3 (confirm before building)
- **R1 — recall matching:** **lexical token-overlap (no LLM, recommended)** vs LLM query-abstraction (1 call
  /recall) vs embeddings. Eval evidence + hot-path cost point to lexical; the others buy ≤1/6 for real cost.
- **R2 — RRF placement + HybridRetriever migration:** extract generic + **migrate HybridRetriever
  (recommended, DRY/replace-don't-deprecate)** vs add generic but leave HybridRetriever (two impls = debt).
- **R3 — episodic grounding:** **split to a separate PR3b (recommended)** vs fold into PR3. It needs a
  write-side change (factual nodes don't carry conversation/turn or an `EpisodicMemoryIds` link yet — the
  `GraphNode` has no such field), so it's a clean separate unit; the greenlight was for recall+RRF specifically.
- **R4 — O(n) recall scan:** **accept now (recommended, matches PR2's documented caveat, fixture-scale)** vs
  add an abstraction-indexed `IKnowledgeGraphStore` primitive. Recall is hotter than write, so flag the scan
  as the primary scaling risk with a TODO, but don't build the index speculatively (YAGNI).

### PR3b (episodic grounding) — ❌ DECIDED NOT TO BUILD (Matt, 2026-07-07)
Would have wired the deferred factual→episodic link so recall attaches raw `EpisodicSegment`s as grounding.
**Skipped after investigation** (agent-mapped the episodic capture + write-context flow) surfaced a poor
trade:
- **Value is thin.** Factual nodes already store the raw fact value verbatim (we index the abstraction but
  store the full content), so grounding mostly adds surrounding-turn chatter that bloats the recall prompt.
  Memora's grounding is the smallest of its gains; the big one (cue-anchor recall) shipped in PR3.
- **Cost is high / touches load-bearing code.** `TurnNumber` is not a first-class value at factual-write time
  and `_scope.ConversationId` is **null** in the background auto-extraction scope. The only place `(conv,turn)`
  survives is baked into the extraction fact key (`"conv:turn:index"`) — extraction-path only; other writers
  (MetaSkillUpdate) use unrelated key formats. Robust linking would need a change to the **`IKnowledgeMemory
  .RememberAsync` public interface** (+ plumb conv/turn through `KnowledgeExtractionBehavior`) plus an
  `IEpisodicSegmentStore` dependency and per-recall I/O on the hot path.
- **Verdict:** fails YAGNI for marginal, partly-redundant value against a core-interface change. The harmonic
  write+read loop is complete and useful without it. Do NOT re-open without a concrete consumer need for raw
  turn grounding. (Episodic segments are still captured by PR2 and cross-linked to `WorkEpisode`; only the
  factual→episodic *read-time join* is unbuilt.)

## Verification (each PR)
`dotnet build src/AgenticHarness.slnx && dotnet test src/AgenticHarness.slnx`; `gitnexus_impact` before
editing `RememberAsync`/`RecallAsync`; `/code-review` + `/simplify` per `rules/review-cadence.md`;
`gitnexus_detect_changes` before commit.

## Decisions — original scope resolved 2026-07-06 (see "Decisions locked" above)
1. Scope → **PR1 + PR2, then eval gate.** 2. Provider → **`NotConfigured` seams only.**
3. Episodic → **include + reconcile with shipped `WorkEpisode` (share seam, distinct records, cross-link).**
4. Retriever → **Semantic only** (superseded on the *recall* side by the 2026-07-07 eval → lexical default; see R1).

**Progress:** PR1 MERGED (#113) · PR2 write path MERGED (#114) · write-side eval + judge fix MERGED (#115/#116)
· eval gate DONE → PR3 GREENLIT. **PR3 (recall + RRF) drafted above, NOT built — awaiting Matt's answers on
R1–R4 before build.**
