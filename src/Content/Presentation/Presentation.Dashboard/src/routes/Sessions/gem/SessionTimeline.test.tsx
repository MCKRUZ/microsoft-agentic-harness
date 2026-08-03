import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { SessionTimeline } from './SessionTimeline';
import { CURRENT_TURN } from './useSessionGemState';
import type { ContextSnapshotEvent, SessionMessageRecord } from '@/api/types';

function snap(
  turnIndex: number,
  loaded: ContextSnapshotEvent['loaded'] = [],
): ContextSnapshotEvent {
  return {
    conversationId: 'c',
    turnIndex,
    turnId: `t-${String(turnIndex).padStart(2, '0')}`,
    ctxAfter: { system: 100, agents: 0, skills: 0, tools: 0, mcp: 0, messages: 50 },
    loaded,
    capturedAtUtc: new Date(2026, 5, 1, 14, 0, turnIndex).toISOString(),
  };
}

function msg(
  turnIndex: number,
  role: 'user' | 'assistant' | 'tool',
  contentPreview: string,
): SessionMessageRecord {
  return {
    id: `m-${turnIndex}-${role}`,
    sessionId: 's-1',
    turnIndex,
    role,
    source: null,
    contentPreview,
    contentFull: null,
    model: null,
    inputTokens: 0,
    outputTokens: 0,
    cacheRead: 0,
    cacheWrite: 0,
    costUsd: 0,
    cacheHitPct: 0,
    toolNames: null,
    createdAt: new Date().toISOString(),
  };
}

describe('SessionTimeline', () => {
  it('renders one row per snapshot with the turn id and role badge', () => {
    render(
      <SessionTimeline
        snapshots={[snap(0), snap(1)]}
        messages={[msg(0, 'user', 'Hi'), msg(1, 'assistant', 'Hello back')]}
        activeTurnIndex={CURRENT_TURN}
        activeCategory={null}
        onTurnScrub={() => {}}
        onLoadedClick={() => {}}
      />,
    );

    expect(screen.getByTestId('timeline-row-0')).toBeTruthy();
    expect(screen.getByTestId('timeline-row-1')).toBeTruthy();
    expect(screen.getByTestId('timeline-row-0-role').textContent).toBe('user');
    expect(screen.getByTestId('timeline-row-1-role').textContent).toBe('assistant');
  });

  it('renders the contentPreview when a turn has a message', () => {
    render(
      <SessionTimeline
        snapshots={[snap(0)]}
        messages={[msg(0, 'user', 'Refactor BillingPipeline.cs')]}
        activeTurnIndex={CURRENT_TURN}
        activeCategory={null}
        onTurnScrub={() => {}}
        onLoadedClick={() => {}}
      />,
    );
    expect(screen.getByTestId('timeline-row-0-excerpt').textContent).toContain(
      'Refactor BillingPipeline.cs',
    );
  });

  it('marks the live-tail row active when activeTurnIndex is CURRENT_TURN', () => {
    render(
      <SessionTimeline
        snapshots={[snap(0), snap(1), snap(2)]}
        messages={[]}
        activeTurnIndex={CURRENT_TURN}
        activeCategory={null}
        onTurnScrub={() => {}}
        onLoadedClick={() => {}}
      />,
    );

    expect(screen.getByTestId('timeline-row-2').getAttribute('data-active')).toBe('true');
    expect(screen.getByTestId('timeline-row-1').getAttribute('data-active')).toBeNull();
  });

  it('marks the explicit active turn row when activeTurnIndex is set', () => {
    render(
      <SessionTimeline
        snapshots={[snap(0), snap(1), snap(2)]}
        messages={[]}
        activeTurnIndex={0}
        activeCategory={null}
        onTurnScrub={() => {}}
        onLoadedClick={() => {}}
      />,
    );
    expect(screen.getByTestId('timeline-row-0').getAttribute('data-active')).toBe('true');
    expect(screen.getByTestId('timeline-row-2').getAttribute('data-active')).toBeNull();
  });

  it('invokes onTurnScrub with the snapshot turnIndex on header click', () => {
    const onTurnScrub = vi.fn();
    render(
      <SessionTimeline
        snapshots={[snap(0), snap(1)]}
        messages={[]}
        activeTurnIndex={CURRENT_TURN}
        activeCategory={null}
        onTurnScrub={onTurnScrub}
        onLoadedClick={() => {}}
      />,
    );
    fireEvent.click(screen.getByTestId('timeline-row-1-scrub'));
    expect(onTurnScrub).toHaveBeenCalledWith(1);
  });

  it('invokes onLoadedClick with the clicked LoadedItem', () => {
    const onLoadedClick = vi.fn();
    const item = { what: 'User message', tokens: 120, cat: 'messages' as const };
    render(
      <SessionTimeline
        snapshots={[snap(0, [item])]}
        messages={[]}
        activeTurnIndex={CURRENT_TURN}
        activeCategory={null}
        onTurnScrub={() => {}}
        onLoadedClick={onLoadedClick}
      />,
    );
    fireEvent.click(screen.getByTestId('timeline-loaded-0-0'));
    expect(onLoadedClick).toHaveBeenCalledWith(item, 0);
  });

  it('renders an empty hint when no snapshots have arrived', () => {
    render(
      <SessionTimeline
        snapshots={[]}
        messages={[]}
        activeTurnIndex={CURRENT_TURN}
        activeCategory={null}
        onTurnScrub={() => {}}
        onLoadedClick={() => {}}
      />,
    );
    expect(screen.getByTestId('session-timeline-empty')).toBeTruthy();
  });
});
