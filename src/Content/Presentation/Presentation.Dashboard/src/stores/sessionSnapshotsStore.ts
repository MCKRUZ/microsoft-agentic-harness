import { create } from 'zustand';
import { useShallow } from 'zustand/react/shallow';
import type { ContextSnapshotEvent } from '@/api/types';

/**
 * Per-session buffer of Foresight context snapshots (PR 3).
 *
 * Snapshots arrive over SignalR (`ContextSnapshot` event) and are also
 * replayed from the `/api/sessions/:id` response on initial load — both paths
 * funnel through the same store so the session-detail timeline reads from one
 * source. Storage is keyed by `conversationId` because the SignalR group, the
 * snapshot event payload, and the session record all share that identifier.
 *
 * Both `hydrateSession` and `appendSnapshot` MERGE by `turnIndex` (last write
 * wins on duplicate index, mirroring the backend `ON CONFLICT DO UPDATE`).
 * Hydration is merge-not-replace so a fetchSessionDetail that lands AFTER a
 * live SignalR snapshot doesn't clobber the in-flight turn — common when a
 * page mount and a turn boundary race within a few hundred ms.
 */
interface SessionSnapshotsState {
  byConversation: Record<string, ContextSnapshotEvent[]>;
  appendSnapshot: (snapshot: ContextSnapshotEvent) => void;
  hydrateSession: (conversationId: string, snapshots: ContextSnapshotEvent[]) => void;
  clearSession: (conversationId: string) => void;
  clearAll: () => void;
}

export const useSessionSnapshotsStore = create<SessionSnapshotsState>((set) => ({
  byConversation: {},

  appendSnapshot: (snapshot) =>
    set((state) => {
      const existing = state.byConversation[snapshot.conversationId] ?? [];
      // Dedupe on turnIndex — a retry/replay of the same turn overwrites the
      // earlier row rather than producing two timeline entries.
      const filtered = existing.filter((s) => s.turnIndex !== snapshot.turnIndex);
      const inserted = [...filtered, snapshot].sort((a, b) => a.turnIndex - b.turnIndex);
      return {
        byConversation: {
          ...state.byConversation,
          [snapshot.conversationId]: inserted,
        },
      };
    }),

  hydrateSession: (conversationId, snapshots) =>
    set((state) => {
      // Merge by turnIndex so an in-flight live snapshot survives a late
      // hydrate. Live writes win on conflict because they reflect the most
      // recent server state — the persisted row catches up on the next
      // turn or refetch.
      const existing = state.byConversation[conversationId] ?? [];
      const liveByTurn = new Map(existing.map((s) => [s.turnIndex, s]));
      const merged = [...snapshots];
      for (let i = 0; i < merged.length; i++) {
        const live = liveByTurn.get(merged[i]!.turnIndex);
        if (live) merged[i] = live;
      }
      // Append any live snapshots whose turnIndex wasn't in the hydrated set.
      const hydratedTurns = new Set(snapshots.map((s) => s.turnIndex));
      for (const live of existing) {
        if (!hydratedTurns.has(live.turnIndex)) merged.push(live);
      }
      merged.sort((a, b) => a.turnIndex - b.turnIndex);
      return {
        byConversation: {
          ...state.byConversation,
          [conversationId]: merged,
        },
      };
    }),

  clearSession: (conversationId) =>
    set((state) => {
      const next = { ...state.byConversation };
      delete next[conversationId];
      return { byConversation: next };
    }),

  clearAll: () => set({ byConversation: {} }),
}));

/**
 * Selector hook: returns the snapshot list for a conversation. Re-renders only
 * when that list changes, not on every other conversation's update.
 */
export function useSessionSnapshots(conversationId: string | undefined): ContextSnapshotEvent[] {
  return useSessionSnapshotsStore(
    useShallow((s) => (conversationId ? (s.byConversation[conversationId] ?? []) : [])),
  );
}
