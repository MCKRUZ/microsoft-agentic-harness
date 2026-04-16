import { describe, it, expect, beforeEach } from 'vitest';
import { useTelemetryStore } from '@/stores/telemetryStore';
import type { SpanData } from '@/types/signalr';

function makeSpan(spanId: string): SpanData {
  return {
    name: `span-${spanId}`,
    traceId: 'trace-1',
    spanId,
    parentSpanId: null,
    conversationId: null,
    startTime: '2024-01-01T00:00:00.000Z',
    durationMs: 10,
    status: 'ok',
    kind: 'internal',
    sourceName: 'test',
    tags: {},
  };
}

describe('useTelemetryStore', () => {
  beforeEach(() => {
    useTelemetryStore.setState({ conversationSpans: {}, globalSpans: [] });
  });

  it('addGlobalSpan caps at MAX_GLOBAL_SPANS (500), dropping oldest entries', () => {
    const { addGlobalSpan } = useTelemetryStore.getState();
    for (let i = 0; i < 501; i++) {
      addGlobalSpan(makeSpan(`span-${i}`));
    }
    const { globalSpans } = useTelemetryStore.getState();
    expect(globalSpans).toHaveLength(500);
    expect(globalSpans[0].spanId).toBe('span-1');
    expect(globalSpans[499].spanId).toBe('span-500');
  });

  it('clearAll resets both conversationSpans and globalSpans to empty', () => {
    const { addConversationSpan, addGlobalSpan, clearAll } = useTelemetryStore.getState();
    addConversationSpan('conv1', makeSpan('s1'));
    addGlobalSpan(makeSpan('s2'));
    clearAll();
    const state = useTelemetryStore.getState();
    expect(state.conversationSpans).toEqual({});
    expect(state.globalSpans).toEqual([]);
  });
});
