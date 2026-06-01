export interface MetricDataPoint {
  timestamp: number;
  value: string;
}

export interface MetricSeries {
  labels: Record<string, string>;
  dataPoints: MetricDataPoint[];
}

export interface MetricsQueryResponse {
  success: boolean;
  resultType: string;
  series: MetricSeries[];
  error?: string;
}

export interface MetricCatalogEntry {
  id: string;
  title: string;
  description: string;
  query: string;
  chartType: string;
  unit: string;
  category: string;
  refreshIntervalSeconds: number;
}

export interface PrometheusHealthResponse {
  healthy: boolean;
  version?: string;
  error?: string;
}

export interface TelemetryEvent {
  type:
    | 'TokenReceived'
    | 'TurnComplete'
    | 'ToolCalled'
    | 'ToolResult'
    | 'BudgetWarning'
    | 'MetricsUpdate'
    | 'Error'
    | 'ConversationStarted'
    | 'ContextSnapshot';
  timestamp: number;
  data: Record<string, unknown>;
}

// ── Foresight context-window primitives (PR 3) ─────────────────────────────
// Mirror Domain.AI.Context shapes — see backend `ContextSnapshotDto`,
// `CategoryBreakdownDto`, `LoadedItemDto`. Property names are part of the
// SignalR + HTTP wire contract.

import type { CategoryBreakdown, CategoryKey } from '@/lib/categories';

/**
 * Per-turn delta item: one user message, assistant message, tool result, or
 * loaded skill that landed in the model's context this turn. Drives the
 * timeline drawer in the session-detail view.
 */
export interface LoadedItem {
  what: string;
  tokens: number;
  cat: CategoryKey;
  ref?: string | null;
}

/**
 * One context snapshot from the SignalR `ContextSnapshot` event or the
 * `/api/sessions/:id` `snapshots[]` array. `ctxAfter` is cumulative; `loaded`
 * is the per-turn delta.
 */
export interface ContextSnapshotEvent {
  conversationId: string;
  turnIndex: number;
  turnId: string;
  ctxAfter: CategoryBreakdown;
  loaded: LoadedItem[];
  capturedAtUtc: string;
}

export interface SessionRecord {
  id: string;
  conversationId: string;
  agentName: string;
  model: string | null;
  startedAt: string;
  endedAt: string | null;
  durationMs: number | null;
  turnCount: number;
  toolCallCount: number;
  subagentCount: number;
  totalInputTokens: number;
  totalOutputTokens: number;
  totalCacheRead: number;
  totalCacheWrite: number;
  totalCostUsd: number;
  cacheHitRate: number;
  status: string;
  errorMessage: string | null;
  createdAt: string;
  /**
   * Latest Foresight context-window breakdown for the conversation, set by
   * the `/api/sessions` list endpoint (PR 3). `null`/`undefined` means no
   * snapshot has been emitted yet — the table mini-bar should render empty.
   */
  breakdown?: CategoryBreakdown | null;
}

export interface SessionMessageRecord {
  id: string;
  sessionId: string;
  turnIndex: number;
  role: string;
  source: string | null;
  contentPreview: string | null;
  model: string | null;
  inputTokens: number;
  outputTokens: number;
  cacheRead: number;
  cacheWrite: number;
  costUsd: number;
  cacheHitPct: number;
  toolNames: string[] | null;
  createdAt: string;
}

export interface ToolExecutionRecord {
  id: string;
  sessionId: string;
  messageId: string | null;
  toolName: string;
  toolSource: string | null;
  durationMs: number | null;
  status: string;
  errorType: string | null;
  resultSize: number | null;
  createdAt: string;
}

export interface SafetyEventRecord {
  id: string;
  sessionId: string;
  phase: string;
  outcome: string;
  category: string | null;
  severity: number | null;
  filterName: string | null;
  createdAt: string;
}

export interface SessionDetail {
  session: SessionRecord;
  messages: SessionMessageRecord[];
  tools: ToolExecutionRecord[];
  safetyEvents: SafetyEventRecord[];
  /**
   * Per-turn context snapshots ordered by `turnIndex` ascending (PR 3).
   * The session-detail page hydrates `sessionSnapshotsStore` from this array
   * so a page refresh during a live conversation replays the timeline.
   */
  snapshots?: ContextSnapshotEvent[];
  /**
   * Convenience: the `ctxAfter` from the last snapshot — equal to the final
   * entry of `snapshots`, surfaced as a top-level field so the hero context
   * bar can render without traversing the array.
   */
  breakdown?: CategoryBreakdown | null;
}
