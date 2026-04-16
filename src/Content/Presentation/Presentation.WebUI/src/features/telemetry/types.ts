import type { SpanData } from '@/types/signalr';

export type { SpanData };

export interface SpanTreeNode extends SpanData {
  children: SpanTreeNode[];
}
