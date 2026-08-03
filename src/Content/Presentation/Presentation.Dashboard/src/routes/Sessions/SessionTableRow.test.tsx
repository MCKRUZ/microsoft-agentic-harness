import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { SessionTableRow } from './SessionTableRow';
// CategoryBreakdown lives in @/lib/categories; api/types imports it but does not re-export
// it, so this was importing a name that module never published.
import type { SessionRecord } from '@/api/types';
import type { CategoryBreakdown } from '@/lib/categories';

function session(overrides: Partial<SessionRecord> = {}): SessionRecord {
  return {
    id: 'sess-1',
    conversationId: 'conv-1',
    agentName: 'CodeAssistant',
    model: 'claude-3-opus',
    startedAt: new Date(2026, 0, 1).toISOString(),
    endedAt: new Date(2026, 0, 1, 1).toISOString(),
    durationMs: 3600000,
    turnCount: 5,
    toolCallCount: 2,
    subagentCount: 0,
    totalInputTokens: 1000,
    totalOutputTokens: 500,
    totalCacheRead: 0,
    totalCacheWrite: 0,
    totalCostUsd: 0.01,
    cacheHitRate: 0,
    status: 'completed',
    errorMessage: null,
    createdAt: new Date(2026, 0, 1).toISOString(),
    breakdown: null,
    ...overrides,
  };
}

const sampleBreakdown: CategoryBreakdown = {
  system: 100,
  agents: 0,
  skills: 0,
  tools: 0,
  mcp: 0,
  messages: 400,
};

function renderRow(s: SessionRecord, onClick = vi.fn()) {
  return render(
    <table>
      <tbody>
        <SessionTableRow session={s} onRowClick={onClick} />
      </tbody>
    </table>,
  );
}

describe('SessionTableRow', () => {
  it('renders the row with the expected data-testid', () => {
    renderRow(session({ id: 'abc' }));
    expect(screen.getByTestId('session-row-abc')).toBeInTheDocument();
  });

  it('renders a ContextBar when the session carries a breakdown', () => {
    renderRow(session({ id: 'abc', breakdown: sampleBreakdown }));
    // session-row-bar-* lives on the ContextBar branch only — fallback rows
    // emit session-row-bar-fallback-* instead.
    expect(screen.getByTestId('session-row-bar-abc')).toBeInTheDocument();
    expect(screen.getByTestId('context-bar')).toBeInTheDocument();
    expect(screen.queryByTestId('session-row-bar-fallback-abc')).toBeNull();
  });

  it('renders the muted fallback rail when breakdown is null', () => {
    renderRow(session({ id: 'abc', breakdown: null }));
    expect(screen.getByTestId('session-row-bar-fallback-abc')).toBeInTheDocument();
    // The ContextBar branch must NOT render — neither the bar nor the
    // bar-wrapper testid should be present.
    expect(screen.queryByTestId('session-row-bar-abc')).toBeNull();
    expect(screen.queryByTestId('context-bar')).toBeNull();
  });

  it('renders the muted fallback rail when breakdown is undefined', () => {
    const s = session({ id: 'abc' });
    delete (s as { breakdown?: unknown }).breakdown;
    renderRow(s as SessionRecord);
    expect(screen.getByTestId('session-row-bar-fallback-abc')).toBeInTheDocument();
  });

  it('forwards the session id to onRowClick when the row is clicked', () => {
    const onClick = vi.fn();
    renderRow(session({ id: 'abc' }), onClick);

    fireEvent.click(screen.getByTestId('session-row-abc'));
    expect(onClick).toHaveBeenCalledWith('abc');
  });

  it('renders the agent name and model', () => {
    renderRow(session({ agentName: 'ResearchAgent', model: 'sonnet-x' }));
    expect(screen.getByText('ResearchAgent')).toBeInTheDocument();
    expect(screen.getByText('sonnet-x')).toBeInTheDocument();
  });

  it('renders `--` for a missing model', () => {
    renderRow(session({ model: null }));
    expect(screen.getByText('--')).toBeInTheDocument();
  });
});
