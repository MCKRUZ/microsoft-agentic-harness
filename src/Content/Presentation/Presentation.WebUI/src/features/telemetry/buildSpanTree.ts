import type { SpanData } from '@/types/signalr';
import type { SpanTreeNode } from './types';

export function buildSpanTree(spans: SpanData[]): SpanTreeNode[] {
  const nodeMap = new Map<string, SpanTreeNode>();

  for (const span of spans) {
    nodeMap.set(span.spanId, { ...span, children: [] });
  }

  const roots: SpanTreeNode[] = [];

  for (const node of nodeMap.values()) {
    const parent = node.parentSpanId !== null ? nodeMap.get(node.parentSpanId) : undefined;
    if (parent) {
      parent.children.push(node);
    } else {
      roots.push(node);
    }
  }

  return roots;
}
