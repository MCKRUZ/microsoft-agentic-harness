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
  type: 'TokenReceived' | 'TurnComplete' | 'ToolCalled' | 'ToolResult' | 'BudgetWarning' | 'MetricsUpdate' | 'Error' | 'ConversationStarted';
  timestamp: number;
  data: Record<string, unknown>;
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
}
