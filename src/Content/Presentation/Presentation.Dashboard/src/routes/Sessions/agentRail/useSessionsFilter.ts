import { useCallback, useMemo, useState } from 'react';
import type { SessionRecord } from '@/api/types';
import type { AgentRollup } from '@/lib/agentRoster';

export interface SessionsFilterState {
  /** Selected agent id, or null when the rail shows "All agents". */
  selectedAgentId: string | null;
  selectAgent: (id: string | null) => void;
  /** Sessions filtered against the current selection. */
  filteredSessions: SessionRecord[];
  /** Convenience flag for the rail's "All agents" affordance. */
  isFiltered: boolean;
}

interface UseSessionsFilterOptions {
  sessions: SessionRecord[];
  roster: AgentRollup[];
}

/**
 * Owns the page-level "which agent is selected?" state and projects the
 * filtered sessions list off the canonical sessions array. Filter is by
 * `agentName` (the field the sessions wire actually carries) joined against
 * the roster's id → name mapping; an unknown id renders zero rows so a stale
 * selection doesn't accidentally widen the table back to "all".
 */
export function useSessionsFilter({
  sessions,
  roster,
}: UseSessionsFilterOptions): SessionsFilterState {
  const [rawSelectedAgentId, setSelectedAgentId] = useState<string | null>(null);

  // Derive the effective selection instead of clearing it through an effect.
  // When the roster identity transitions and the previously-selected id is no
  // longer in it, the selection reads as null ("All agents") this render — no
  // setState-in-effect, no extra paint. This covers the cold-load race where
  // the fallback roster (id = agentName) gets clicked, then the registry
  // resolves and ids switch to canonical 'agent-xxx' values.
  const selectedAgentId = useMemo(
    () =>
      rawSelectedAgentId !== null && roster.some((a) => a.id === rawSelectedAgentId)
        ? rawSelectedAgentId
        : null,
    [rawSelectedAgentId, roster],
  );

  const filteredSessions = useMemo(() => {
    // null id → no filter; full list passes through.
    if (selectedAgentId === null) return sessions;
    const selected = roster.find((a) => a.id === selectedAgentId) ?? null;
    // Selection points at an id the roster doesn't know — return empty so the
    // user sees the empty state. (Derivation above already nulls unknown ids,
    // so this is a belt-and-suspenders guard.)
    if (selected === null) return [];
    // Sessions may carry the agent's display name ("Default Agent") OR its
    // slug/id ("default") depending on which code path persisted them.
    // Normalise both sides and accept either form. Mirrors the join in
    // buildAgentRoster so the tile count and the filtered list stay in sync.
    const nameKey = normalizeKey(selected.name);
    const idKey = normalizeKey(selected.id);
    return sessions.filter((s) => {
      const k = normalizeKey(s.agentName);
      return k === nameKey || k === idKey;
    });
  }, [sessions, selectedAgentId, roster]);

  const selectAgent = useCallback((id: string | null) => {
    setSelectedAgentId(id);
  }, []);

  return {
    selectedAgentId,
    selectAgent,
    filteredSessions,
    isFiltered: selectedAgentId !== null,
  };
}

function normalizeKey(value: string): string {
  return value.trim().toLowerCase().replace(/\s+/g, ' ');
}
