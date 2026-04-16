import { describe, it, expect } from 'vitest';
import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { renderWithProviders } from '@/test/utils';
import { SpanNode } from '../SpanNode';
import type { SpanTreeNode } from '../types';

function makeNode(status: 'ok' | 'error' | 'unset', tags: Record<string, string> = {}): SpanTreeNode {
  return {
    name: 'test-span',
    traceId: 'trace-1',
    spanId: 'span-1',
    parentSpanId: null,
    conversationId: null,
    startTime: '2024-01-01T00:00:00.000Z',
    durationMs: 100,
    status,
    kind: 'internal',
    sourceName: 'test',
    tags,
    children: [],
  };
}

describe('SpanNode', () => {
  it('renders green status indicator for ok status', () => {
    renderWithProviders(<SpanNode node={makeNode('ok')} rootDurationMs={100} depth={0} />);
    expect(document.querySelector('.bg-green-500')).toBeTruthy();
  });

  it('renders red status indicator for error status', () => {
    renderWithProviders(<SpanNode node={makeNode('error')} rootDurationMs={100} depth={0} />);
    expect(document.querySelector('.bg-red-500')).toBeTruthy();
  });

  it('renders grey status indicator for unset status', () => {
    renderWithProviders(<SpanNode node={makeNode('unset')} rootDurationMs={100} depth={0} />);
    expect(document.querySelector('.bg-gray-400')).toBeTruthy();
  });

  it('Clicking SpanNode expands SpanDetail showing tags as key-value pairs', async () => {
    const user = userEvent.setup();
    const node = makeNode('ok', { 'http.method': 'GET', 'http.url': '/api/test' });
    renderWithProviders(<SpanNode node={node} rootDurationMs={100} depth={0} />);
    await user.click(screen.getByRole('button'));
    expect(screen.getByText('http.method')).toBeInTheDocument();
    expect(screen.getByText('GET')).toBeInTheDocument();
  });
});
