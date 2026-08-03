import { describe, it, expect } from 'vitest';
import { act, renderHook } from '@testing-library/react';
import {
  useSessionGemState,
  CURRENT_TURN,
} from './useSessionGemState';
import type { ContextSnapshotEvent, LoadedItem } from '@/api/types';
import type { CategoryBreakdown } from '@/lib/categories';

function snap(
  turnIndex: number,
  ctxAfter: CategoryBreakdown,
  loaded: LoadedItem[] = [],
): ContextSnapshotEvent {
  return {
    conversationId: 'c',
    turnIndex,
    turnId: `t-${String(turnIndex).padStart(2, '0')}`,
    ctxAfter,
    loaded,
    capturedAtUtc: new Date(2026, 0, 1, 0, turnIndex).toISOString(),
  };
}

const ZERO: CategoryBreakdown = { system: 0, agents: 0, skills: 0, tools: 0, mcp: 0, messages: 0 };

describe('useSessionGemState — initial state', () => {
  it('starts at CURRENT_TURN with no scrub, no filter, no drawer', () => {
    const { result } = renderHook(() =>
      useSessionGemState({ snapshots: [], messages: [], tools: [] }),
    );
    expect(result.current.activeTurnIndex).toBe(CURRENT_TURN);
    expect(result.current.isScrubbed).toBe(false);
    expect(result.current.activeCategory).toBeNull();
    expect(result.current.drawerItem).toBeNull();
  });

  it('falls back to fallbackBreakdown when no snapshots have arrived', () => {
    const fallback: CategoryBreakdown = { ...ZERO, system: 42 };
    const { result } = renderHook(() =>
      useSessionGemState({
        snapshots: [],
        fallbackBreakdown: fallback,
        messages: [],
        tools: [],
      }),
    );
    expect(result.current.displayedBreakdown).toEqual(fallback);
  });
});

describe('useSessionGemState — displayedBreakdown', () => {
  it('returns the last snapshot ctxAfter when at the live tail', () => {
    const s0 = snap(0, { ...ZERO, messages: 100 });
    const s1 = snap(1, { ...ZERO, messages: 250 });

    const { result } = renderHook(() =>
      useSessionGemState({ snapshots: [s0, s1], messages: [], tools: [] }),
    );

    expect(result.current.displayedBreakdown).toEqual(s1.ctxAfter);
    expect(result.current.isScrubbed).toBe(false);
  });

  it('switches to the scrubbed snapshot ctxAfter and marks isScrubbed=true', () => {
    const s0 = snap(0, { ...ZERO, messages: 100 });
    const s1 = snap(1, { ...ZERO, messages: 250 });

    const { result } = renderHook(() =>
      useSessionGemState({ snapshots: [s0, s1], messages: [], tools: [] }),
    );

    act(() => result.current.setActiveTurnIndex(0));
    expect(result.current.displayedBreakdown).toEqual(s0.ctxAfter);
    expect(result.current.isScrubbed).toBe(true);
  });

  it('jumpToCurrent returns the hero to the live tail', () => {
    const s0 = snap(0, { ...ZERO, messages: 100 });
    const s1 = snap(1, { ...ZERO, messages: 250 });

    const { result } = renderHook(() =>
      useSessionGemState({ snapshots: [s0, s1], messages: [], tools: [] }),
    );

    act(() => result.current.setActiveTurnIndex(0));
    expect(result.current.isScrubbed).toBe(true);

    act(() => result.current.jumpToCurrent());
    expect(result.current.activeTurnIndex).toBe(CURRENT_TURN);
    expect(result.current.isScrubbed).toBe(false);
    expect(result.current.displayedBreakdown).toEqual(s1.ctxAfter);
  });

  it('isScrubbed is false when activeTurnIndex equals the live tail index', () => {
    const s0 = snap(0, { ...ZERO, messages: 100 });
    const s1 = snap(1, { ...ZERO, messages: 250 });

    const { result } = renderHook(() =>
      useSessionGemState({ snapshots: [s0, s1], messages: [], tools: [] }),
    );

    act(() => result.current.setActiveTurnIndex(1));
    expect(result.current.isScrubbed).toBe(false);
  });
});

describe('useSessionGemState — drawer', () => {
  const userMsg: LoadedItem = { what: 'User message', tokens: 100, cat: 'messages' };
  const skill: LoadedItem = { what: 'rules/testing', tokens: 250, cat: 'skills' };
  const s0 = snap(0, { ...ZERO, messages: 100 }, [userMsg]);
  const s1 = snap(1, { ...ZERO, messages: 100, skills: 250 }, [skill]);

  it('openDrawer builds a target with content, name, path, and loadedIndex', () => {
    const { result } = renderHook(() =>
      useSessionGemState({
        snapshots: [s0, s1],
        messages: [
          {
            id: 'm-0', sessionId: 's', turnIndex: 0, role: 'user',
            source: null, contentPreview: 'Hello', contentFull: null, model: null,
            inputTokens: 0, outputTokens: 0, cacheRead: 0, cacheWrite: 0,
            costUsd: 0, cacheHitPct: 0, toolNames: null,
            createdAt: new Date().toISOString(),
          },
        ],
        tools: [],
      }),
    );

    act(() => result.current.openDrawer(userMsg, 'timeline', 0));
    const t = result.current.drawerItem!;
    expect(t).not.toBeNull();
    expect(t.item).toBe(userMsg);
    expect(t.content.body).toContain('Hello');
    expect(t.content.role).toBe('user');
    expect(t.loadedIndex).toBe(0);
    expect(t.path).toBe('—');
  });

  it('closeDrawer clears drawerItem', () => {
    const { result } = renderHook(() =>
      useSessionGemState({ snapshots: [s0, s1], messages: [], tools: [] }),
    );

    act(() => result.current.openDrawer(skill, 'hero', 1));
    expect(result.current.drawerItem).not.toBeNull();
    act(() => result.current.closeDrawer());
    expect(result.current.drawerItem).toBeNull();
  });

  it('walkDrawer cycles within the active category lane', () => {
    const m0: LoadedItem = { what: 'A', tokens: 1, cat: 'messages' };
    const m1: LoadedItem = { what: 'B', tokens: 2, cat: 'messages' };
    const m2: LoadedItem = { what: 'C', tokens: 4, cat: 'messages' };
    const sa = snap(0, { ...ZERO, messages: 1 }, [m0]);
    const sb = snap(1, { ...ZERO, messages: 3 }, [m1]);
    const sc = snap(2, { ...ZERO, messages: 7 }, [m2]);

    const { result } = renderHook(() =>
      useSessionGemState({
        snapshots: [sa, sb, sc],
        messages: [],
        tools: [],
      }),
    );

    act(() => result.current.openDrawer(m1, 'timeline', 1));
    expect(result.current.drawerItem?.item).toBe(m1);

    act(() => result.current.walkDrawer(1));
    expect(result.current.drawerItem?.item).toBe(m2);

    act(() => result.current.walkDrawer(1));
    // Wraps to the start of the category lane.
    expect(result.current.drawerItem?.item).toBe(m0);

    act(() => result.current.walkDrawer(-1));
    // Wraps back to the end.
    expect(result.current.drawerItem?.item).toBe(m2);
  });
});
