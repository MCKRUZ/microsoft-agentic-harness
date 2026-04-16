import { describe, it, expect } from 'vitest';
import { buildSpanTree } from '../buildSpanTree';
import type { SpanData } from '@/types/signalr';

function makeSpan(spanId: string, parentSpanId: string | null, traceId = 'trace-1'): SpanData {
  return {
    name: `span-${spanId}`,
    traceId,
    spanId,
    parentSpanId,
    conversationId: null,
    startTime: '2024-01-01T00:00:00.000Z',
    durationMs: 100,
    status: 'ok',
    kind: 'internal',
    sourceName: 'test',
    tags: {},
  };
}

describe('buildSpanTree', () => {
  it('returns empty array for empty input', () => {
    expect(buildSpanTree([])).toEqual([]);
  });

  it('nests child spans under their parent by parentSpanId', () => {
    const spans = [makeSpan('root', null), makeSpan('child', 'root')];
    const result = buildSpanTree(spans);
    expect(result).toHaveLength(1);
    expect(result[0].spanId).toBe('root');
    expect(result[0].children).toHaveLength(1);
    expect(result[0].children[0].spanId).toBe('child');
  });

  it('handles root spans with null parentSpanId', () => {
    const spans = [makeSpan('root', null)];
    const result = buildSpanTree(spans);
    expect(result).toHaveLength(1);
    expect(result[0].parentSpanId).toBeNull();
    expect(result[0].children).toEqual([]);
  });

  it('handles multiple disjoint trace trees', () => {
    const spans = [
      makeSpan('root1', null, 'trace-1'),
      makeSpan('root2', null, 'trace-2'),
      makeSpan('child1', 'root1', 'trace-1'),
    ];
    const result = buildSpanTree(spans);
    expect(result).toHaveLength(2);
    const root1 = result.find((r) => r.spanId === 'root1');
    expect(root1?.children).toHaveLength(1);
  });

  it('result is stable for same input', () => {
    const spans = [makeSpan('root', null), makeSpan('child', 'root')];
    const result1 = buildSpanTree(spans);
    const result2 = buildSpanTree(spans);
    expect(result1[0].spanId).toBe(result2[0].spanId);
    expect(result1[0].children[0].spanId).toBe(result2[0].children[0].spanId);
  });
});
