/**
 * Shared PromQL for LLM cost panels.
 *
 * Prompt caching makes the per-call cost *estimate* (priced from the static pricing table) overstate
 * spend, because cached prompt tokens are billed far below the full input rate. On providers that
 * report a real, already-discounted per-call cost — OpenRouter, surfaced as
 * `agent.tokens.cost_actual` — we prefer that and fall back to the estimate everywhere else.
 *
 * Every cost panel (catalog tiles and route-embedded queries alike) builds its query from these
 * helpers so the dashboard never shows the inflated estimate on one panel and the true spend on
 * another. `cost_actual` carries the same `model` / `agent_name` tags as `cost_estimated`, so the
 * grouped variants behave identically to the estimate-only queries they replace.
 */
const ACTUAL = 'agentic_harness_agent_tokens_cost_actual_total';
const ESTIMATED = 'agentic_harness_agent_tokens_cost_estimated_total';

/** Scalar total spend, actual-preferred. Falls back to 0 so a stat panel renders before any data. */
export const costTotalQuery = `sum(${ACTUAL}) or sum(${ESTIMATED}) or vector(0)`;

/** USD/hour burn rate over a 5m window, actual-preferred. */
export const costRateQuery =
  `rate(${ACTUAL}[5m]) * 3600 or rate(${ESTIMATED}[5m]) * 3600 or vector(0)`;

/**
 * Spend grouped by a label (e.g. `model`, `agent_name`), actual-preferred. The `or` unions per
 * label set, so each group shows its actual cost when present and its estimate otherwise.
 */
export const costByQuery = (label: string): string =>
  `sum by (${label}) (${ACTUAL}) or sum by (${label}) (${ESTIMATED})`;
