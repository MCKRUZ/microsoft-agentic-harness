import { describe, it, expect, beforeEach } from 'vitest';
import { useSessionSnapshotsStore } from './sessionSnapshotsStore';
import type { ContextSnapshotEvent } from '@/api/types';

function makeSnapshot(
  conversationId: string,
  turnIndex: number,
  messagesTokens = 100,
): ContextSnapshotEvent {
  return {
    conversationId,
    turnIndex,
    turnId: `t-${String(turnIndex).padStart(2, '0')}`,
    ctxAfter: {
      system: 1000,
      agents: 0,
      skills: 0,
      tools: 0,
      mcp: 0,
      messages: messagesTokens,
    },
    loaded: [{ what: `Turn ${turnIndex}`, tokens: messagesTokens, cat: 'messages' }],
    capturedAtUtc: new Date(2026, 5, 1, 12, turnIndex).toISOString(),
  };
}

describe('sessionSnapshotsStore', () => {
  beforeEach(() => {
    useSessionSnapshotsStore.setState({ byConversation: {} });
  });

  it('starts empty', () => {
    expect(useSessionSnapshotsStore.getState().byConversation).toEqual({});
  });

  it('appendSnapshot adds a snapshot keyed by conversationId', () => {
    useSessionSnapshotsStore.getState().appendSnapshot(makeSnapshot('conv-1', 0));
    const list = useSessionSnapshotsStore.getState().byConversation['conv-1'];
    expect(list).toHaveLength(1);
    expect(list![0]!.turnIndex).toBe(0);
  });

  it('appendSnapshot keeps snapshots sorted by turnIndex', () => {
    const store = useSessionSnapshotsStore.getState();
    store.appendSnapshot(makeSnapshot('conv-1', 2));
    store.appendSnapshot(makeSnapshot('conv-1', 0));
    store.appendSnapshot(makeSnapshot('conv-1', 1));

    const indexes = useSessionSnapshotsStore
      .getState()
      .byConversation['conv-1']!
      .map((s) => s.turnIndex);
    expect(indexes).toEqual([0, 1, 2]);
  });

  it('appendSnapshot dedupes on turnIndex (last write wins)', () => {
    const store = useSessionSnapshotsStore.getState();
    store.appendSnapshot(makeSnapshot('conv-1', 0, 100));
    store.appendSnapshot(makeSnapshot('conv-1', 1, 200));
    store.appendSnapshot(makeSnapshot('conv-1', 1, 999));

    const list = useSessionSnapshotsStore.getState().byConversation['conv-1']!;
    expect(list).toHaveLength(2);
    expect(list[1]!.ctxAfter.messages).toBe(999);
  });

  it('appendSnapshot isolates conversations from each other', () => {
    const store = useSessionSnapshotsStore.getState();
    store.appendSnapshot(makeSnapshot('conv-1', 0));
    store.appendSnapshot(makeSnapshot('conv-2', 0));
    store.appendSnapshot(makeSnapshot('conv-2', 1));

    const state = useSessionSnapshotsStore.getState();
    expect(state.byConversation['conv-1']).toHaveLength(1);
    expect(state.byConversation['conv-2']).toHaveLength(2);
  });

  it('hydrateSession MERGES live snapshots with the hydrated set', () => {
    // Race: SignalR delivered turn 99 (live), then fetchSessionDetail returns
    // a persisted timeline [0, 1] that pre-dates turn 99. Live turn 99 must
    // survive.
    const store = useSessionSnapshotsStore.getState();
    store.appendSnapshot(makeSnapshot('conv-1', 99));

    store.hydrateSession('conv-1', [
      makeSnapshot('conv-1', 0),
      makeSnapshot('conv-1', 1),
    ]);

    const list = useSessionSnapshotsStore.getState().byConversation['conv-1']!;
    expect(list.map((s) => s.turnIndex)).toEqual([0, 1, 99]);
  });

  it('hydrateSession preserves live snapshot when both sources have same turnIndex', () => {
    // Live appended turn 1 with messagesTokens=999; persisted hydrate has the
    // older value 100. Live wins because it reflects current server state.
    const store = useSessionSnapshotsStore.getState();
    store.appendSnapshot(makeSnapshot('conv-1', 1, 999));

    store.hydrateSession('conv-1', [
      makeSnapshot('conv-1', 0, 100),
      makeSnapshot('conv-1', 1, 100),
    ]);

    const list = useSessionSnapshotsStore.getState().byConversation['conv-1']!;
    expect(list).toHaveLength(2);
    expect(list[1]!.ctxAfter.messages).toBe(999);
  });

  it('hydrateSession sorts incoming snapshots by turnIndex', () => {
    useSessionSnapshotsStore.getState().hydrateSession('conv-1', [
      makeSnapshot('conv-1', 2),
      makeSnapshot('conv-1', 0),
      makeSnapshot('conv-1', 1),
    ]);

    const indexes = useSessionSnapshotsStore
      .getState()
      .byConversation['conv-1']!
      .map((s) => s.turnIndex);
    expect(indexes).toEqual([0, 1, 2]);
  });

  it('clearSession removes a single conversation', () => {
    const store = useSessionSnapshotsStore.getState();
    store.appendSnapshot(makeSnapshot('conv-1', 0));
    store.appendSnapshot(makeSnapshot('conv-2', 0));

    store.clearSession('conv-1');

    const state = useSessionSnapshotsStore.getState();
    expect(state.byConversation['conv-1']).toBeUndefined();
    expect(state.byConversation['conv-2']).toHaveLength(1);
  });

  it('clearAll empties the store', () => {
    const store = useSessionSnapshotsStore.getState();
    store.appendSnapshot(makeSnapshot('conv-1', 0));
    store.appendSnapshot(makeSnapshot('conv-2', 0));

    store.clearAll();

    expect(useSessionSnapshotsStore.getState().byConversation).toEqual({});
  });
});
