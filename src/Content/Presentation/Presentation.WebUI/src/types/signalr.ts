export interface SpanData {
  name: string;
  traceId: string;
  spanId: string;
  parentSpanId: string | null;
  conversationId: string | null;
  startTime: string;
  durationMs: number;
  status: 'unset' | 'ok' | 'error';
  statusDescription?: string;
  kind: string;
  sourceName: string;
  tags: Record<string, string>;
}
