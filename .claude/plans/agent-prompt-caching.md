# Feature Plan: Agent Prompt Caching + Telemetry

**Status:** Greenlit for write-up (not built). Spike completed 2026-06-22 — caching viability **proven** on the live provider.
**Goal (Matt's words):** "Enable caching on our agents, and track it."
**Scope:** Separate from the dashboard-label PR. Two workstreams → two PRs.

---

## 1. Why

Every agent turn resends the full system prompt + tool definitions at full input price, even though that prefix is stable across turns. Provider-side prompt caching lets the model reuse that prefix at ~10% of the input cost.

**Spike evidence (2026-06-22, live, two identical calls 2s apart):**

| | Call 1 (write) | Call 2 (read) |
|---|---|---|
| Prompt tokens | 18,054 | 18,054 |
| Cache write | 18,041 | 0 |
| Cache read | 0 | 18,041 |
| Cost | $0.067 | $0.0055 |

≈ **92% cheaper** on the cached prefix (~12×). This is the win the Cache tiles were built to show but never received data for.

---

## 2. Provider reality (corrected by the spike)

The live dev config does **not** use Azure AI Foundry's Anthropic endpoint. It uses **OpenRouter**:

- Endpoint: `https://openrouter.ai/api/v1` (OpenAI-compatible chat completions)
- `ClientType: OpenAI` → `ChatClientFactory.GetOpenAIChatClientAsync`
- Model: `anthropic/claude-sonnet-4.6`
- Caching mechanism: Anthropic `cache_control: {type: ephemeral}` breakpoint placed on a **content block** inside the OpenAI-format request.
- Usage fields returned (OpenAI/OpenRouter shape): `prompt_tokens_details.cached_tokens` (read), `prompt_tokens_details.cache_write_tokens` (write).

The code still contains a native-Anthropic/Foundry path (`GetAnthropicChatClient` + `AzureFoundryRewritingHandler`) that uses Anthropic-shaped field names (`cache_read_input_tokens` / `cache_creation_input_tokens`). The template must handle **both** shapes so a consumer on either provider gets working caching + telemetry.

---

## 3. Workstream 1 — Enable caching (inject the breakpoint)

**What:** On each outgoing request, mark the stable prefix (system prompt, and ideally the tool-definition block) with `cache_control: {type: ephemeral}` so the provider caches it.

**Seam — two candidate approaches:**

- **(A) DelegatingChatClient** that rewrites `ChatOptions`/messages before send (mirrors the existing `ModelBoundChatClient`). Cleaner/typed, but **unverified**: Microsoft.Extensions.AI's OpenAI mapping may not surface a per-content-block `cache_control` field. Needs a spike to confirm the marker survives the abstraction (e.g. via `AdditionalProperties` / `RawRepresentationFactory`).
- **(B) DelegatingHandler at the HTTP layer** that injects `cache_control` directly into the outgoing JSON body. Provider-agnostic and robust (we already use this pattern in `AzureFoundryRewritingHandler`), at the cost of operating on raw JSON rather than typed objects.

**Recommendation:** verify (A) first (one short spike). If the marker doesn't pass through cleanly, fall back to (B). The spike already proves (B) works end-to-end.

**Constraints to encode (caching silently no-ops if violated):**
- Minimum cacheable size (~1024 tokens Sonnet/Opus; ~2048 Haiku) — *verify current numbers against Anthropic docs*.
- ~5-minute ephemeral TTL.
- Prefix must be byte-identical turn-to-turn. Place the breakpoint after the last stable block; never let dynamic content (timestamps, per-turn data) sit before it.

**Config toggle:** `AppConfig:AI:AgentFramework:EnablePromptCaching` (or per-agent), default on for supported providers, so consumers can disable it. Avoid caching on single-shot/tiny-prefix calls where the write premium (1.25×) isn't recouped.

---

## 4. Workstream 2 — Track caching (capture + emit metrics)

**What:** Read the provider's cache token counts from the response and feed the harness's existing cache metrics so the dashboard tiles move.

**The gap:** `LlmTokenTrackingProcessor` reads Anthropic-named **span attributes** (`gen_ai.usage.cache_read_input_tokens` / `cache_creation_input_tokens`). The OpenAI/OpenRouter client does **not** populate those — its cache counts arrive as `prompt_tokens_details.cached_tokens` / `cache_write_tokens`, surfaced by MEAI as `UsageDetails.AdditionalCounts` (or only in the raw response). So today the metrics never receive cache data → tiles read 0.

**Work:** add an extraction seam (likely in/near `LlmUsageCapture`) that:
- Reads cache read/write counts from MEAI `UsageDetails.AdditionalCounts` (and/or the raw OpenRouter usage object).
- Normalizes both provider shapes (OpenRouter `cached_tokens`/`cache_write_tokens` **and** native Anthropic `cache_read_input_tokens`/`cache_creation_input_tokens`) into one internal representation.
- Feeds the existing instruments: `CacheReadTokens`, `CacheWriteTokens`, `CacheHitRate`, `CacheSavings`.

Already-fixed model label (`model`) and the savings math are in place — this workstream just supplies the inputs.

---

## 5. Test strategy

- **Cheap / CI (unit):**
  - Outgoing request carries `cache_control` on the stable prefix (assert on request object; no network). *Write first, watch it fail (TDD entry point).*
  - Usage extraction maps both provider shapes → cache metrics (synthetic `UsageDetails`/response; no network). Extend `LlmTokenTrackingProcessorTests` / `LlmUsageCaptureTests`.
- **Gated / live (paid):** productize the spike — two identical large requests; assert call 1 writes cache, call 2 reads it. `[Trait("Category","LiveLlm")]` + skip when no API key (same gating convention as the Postgres/Docker `SkippableFact` tests).
- **Human smoke:** multi-turn conversation; watch Cache Write spike on turn 1, Cache Read + Efficiency climb on later turns.
- **Stability guard:** reuse `Sha256PromptCacheTracker` to detect when the cacheable prefix changes (a "cache break") — protects against future edits silently busting caching.

---

## 6. Risks / unknowns

1. **`cache_control` passthrough via the MEAI abstraction** (drives Seam A vs B). Resolve with a short spike before committing to A.
2. **Provider variance** — OpenRouter vs native Anthropic field names. Mitigated by normalizing both in WS2.
3. **Prefix stability** — dynamic content before the breakpoint silently disables caching. Mitigated by breakpoint placement + the stability guard.
4. **Write premium** — cache writes cost ~1.25× input; net loss if prefixes are rarely reused (single-turn). Mitigated by the config toggle / only caching large stable multi-turn prefixes.

---

## 7. Effort (facts, not selection criteria)

- WS1 (enable): 1 PR. Small–medium; hinges on the Seam A/B spike.
- WS2 (track): 1 PR. Small–medium; mostly the extraction + normalization + tests.
- Plus the gated live test and a dashboard smoke check.

Sequence: WS1 first (caching real), then WS2 (make it visible). Each its own review.
