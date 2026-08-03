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
  const [selectedAgentId, setSelectedAgentId] = useState<string | null>(null);

  // Handles the cold-load race where the fallback roster (id = agentName) gets clicked, then
  // the registry resolves and ids switch to canonical 'agent-xxx' values — the stale id would
  // otherwise produce a silent empty list with no active tile.
  //
  // DERIVED during render rather than corrected by an effect. The effect form set state from
  // inside useEffect, which costs a second render pass and, in between, paints exactly the
  // silent-empty-list state it exists to prevent. Deriving means the roster and the selection
  // can never disagree in a committed render, so the flash cannot occur at all.
  //
  // The raw id stays in state deliberately: a roster that briefly empties during a refetch
  // then returns restores the user's selection instead of silently discarding it.
  const effectiveAgentId =
    selectedAgentId !== null && roster.some((a) => a.id === selectedAgentId)
      ? selectedAgentId
      : null;

  const filteredSessions = useMemo(() => {
    // null id → no filter; full list passes through.
    if (effectiveAgentId === null) return sessions;
    const selected = roster.find((a) => a.id === effectiveAgentId) ?? null;
    // Unreachable now that effectiveAgentId is only non-null when the roster contains it,
    // but kept as a total branch rather than a non-null assertion.
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
  }, [sessions, effectiveAgentId, roster]);

  const selectAgent = useCallback((id: string | null) => {
    setSelectedAgentId(id);
  }, []);

  // The EFFECTIVE id is what callers see, so the highlighted rail tile and the rows in the
  // table are always describing the same selection.
  return {
    selectedAgentId: effectiveAgentId,
    selectAgent,
    filteredSessions,
    isFiltered: effectiveAgentId !== null,
  };
}

function normalizeKey(value: string): string {
  return value.trim().toLowerCase().replace(/\s+/g, ' ');
}
