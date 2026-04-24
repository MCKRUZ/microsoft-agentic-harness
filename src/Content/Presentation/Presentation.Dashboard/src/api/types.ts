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
