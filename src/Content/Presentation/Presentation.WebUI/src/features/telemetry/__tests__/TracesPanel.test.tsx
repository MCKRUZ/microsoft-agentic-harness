import { describe, it, expect } from 'vitest';
import { screen } from '@testing-library/react';
import { renderWithProviders } from '@/test/utils';
import { TracesPanel } from '../TracesPanel';
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

describe('TracesPanel', () => {
  it('with empty spans array renders empty state placeholder text', () => {
    renderWithProviders(<TracesPanel spans={[]} />);
    expect(screen.getByText(/No traces yet/i)).toBeInTheDocument();
  });

  it('renders correct number of root SpanTree components for disjoint traces', () => {
    const spans = [
      makeSpan('root1', null, 'trace-1'),
      makeSpan('root2', null, 'trace-2'),
      makeSpan('root3', null, 'trace-3'),
    ];
    renderWithProviders(<TracesPanel spans={spans} />);
    expect(document.querySelectorAll('[data-testid="span-tree"]')).toHaveLength(3);
  });
});
