import { useMemo } from 'react';
import { useNavigate } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { fetchSessions } from '@/api/sessions';
import { fetchAgents } from '@/api/agents';
import { useTimeRangeStore } from '@/stores/timeRangeStore';
import { KpiCard } from '@/components/panels/KpiCard';
import { PanelCard } from '@/components/panels/PanelCard';
import { PanelGrid } from '@/components/panels/PanelGrid';
import { LoadingSkeleton } from '@/components/panels/LoadingSkeleton';
import { EmptyState } from '@/components/panels/EmptyState';
import { PageHeader } from '@/components/primitives/PageHeader';
import { buildAgentRoster } from '@/lib/agentRoster';
import { AgentRail } from './agentRail/AgentRail';
import { useSessionsFilter } from './agentRail/useSessionsFilter';
import { SessionTableRow } from './SessionTableRow';
import { formatDurationSeconds } from './format';
import type { SessionRecord } from '@/api/types';

interface SessionKpis {
  total: number;
  active: number;
  avgTurns: number;
  avgDurationSec: number;
}

/**
 * Derives the four header KPIs from the same session list the table renders,
 * so the numbers match what the user sees row-for-row. A Prometheus-backed
 * derivation would split the source of truth and surface zeros whenever the
 * metric pipeline lags or isn't configured.
 */
function deriveKpis(sessions: SessionRecord[]): SessionKpis {
  if (sessions.length === 0) {
    return { total: 0, active: 0, avgTurns: 0, avgDurationSec: 0 };
  }
  let active = 0;
  let turnsSum = 0;
  let durationMsSum = 0;
  let durationCount = 0;
  for (const s of sessions) {
    if (s.status?.toLowerCase() === 'active' || s.endedAt === null) active++;
    turnsSum += s.turnCount;
    if (typeof s.durationMs === 'number') {
      durationMsSum += s.durationMs;
      durationCount++;
    }
  }
  return {
    total: sessions.length,
    active,
    avgTurns: turnsSum / sessions.length,
    avgDurationSec: durationCount > 0 ? durationMsSum / durationCount / 1000 : 0,
  };
}

export default function SessionsPage() {
  const navigate = useNavigate();
  const preset = useTimeRangeStore((s) => s.preset);
  const customStart = useTimeRangeStore((s) => s.customStart);
  const customEnd = useTimeRangeStore((s) => s.customEnd);
  const getRange = useTimeRangeStore((s) => s.getRange);
  // getRange() reads preset/customStart/customEnd from the store; its identity
  // is stable, so those three values are the real inputs that must re-trigger
  // the range computation. The exhaustive-deps rule flags them as "unnecessary"
  // because they aren't referenced in the callback body, but dropping them would
  // freeze the range on first render — they are deliberate.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  const { start, end } = useMemo(() => getRange(), [preset, customStart, customEnd]);

  const sessionsQuery = useQuery({
    queryKey: ['sessions-list', start, end],
    queryFn: () => fetchSessions(50, 0, undefined, start, end),
    staleTime: 30_000,
  });

  // Separate query so the rail can render once agents come back; refetches
  // on its own 60s cadence and survives time-range changes (agents are not
  // time-bound). Single retry — registry failures are typically auth/role
  // problems where hammering doesn't help; surface via `degraded` instead.
  const agentsQuery = useQuery({
    queryKey: ['agents'],
    queryFn: fetchAgents,
    staleTime: 60_000,
    retry: 1,
  });

  // Stabilise identity: `?? []` would mint a fresh array every render while the
  // query is empty, re-running the roster memo (and its dependents) needlessly.
  const sessions = useMemo(() => sessionsQuery.data ?? [], [sessionsQuery.data]);
  const roster = useMemo(
    () => buildAgentRoster(agentsQuery.data ?? [], sessions),
    [agentsQuery.data, sessions],
  );

  const filter = useSessionsFilter({ sessions, roster });

  // KPIs reflect the same per-agent scope the table shows: full list when
  // "All agents" is active, filtered list when a tile is selected.
  const kpis = useMemo(() => deriveKpis(filter.filteredSessions), [filter.filteredSessions]);
  const isKpiLoading = sessionsQuery.isLoading;

  return (
    <div className="space-y-6">
      <PageHeader title="Sessions" subtitle="Agents in flight + per-session context-window composition" />

      {isKpiLoading ? (
        <PanelGrid columns={4}>
          {Array.from({ length: 4 }).map((_, i) => <LoadingSkeleton key={i} />)}
        </PanelGrid>
      ) : (
        <PanelGrid columns={4}>
          <KpiCard
            title="Total Sessions"
            description="Number of agent conversation sessions started in the selected time range. Shows 0 when no conversations have been initiated."
            value={kpis.total.toFixed(0)}
          />
          <KpiCard
            title="Active Sessions"
            description="Sessions currently in progress with an open connection. Shows 0 when all sessions have completed or no agents are running."
            value={kpis.active.toFixed(0)}
          />
          <KpiCard
            title="Avg Turns/Session"
            description="Mean number of user-agent exchange turns per session. Shows 0 when no completed sessions exist in the selected time range."
            value={kpis.avgTurns.toFixed(1)}
          />
          <KpiCard
            title="Avg Duration"
            description="Mean wall-clock time from session start to session end. Shows 0 when no completed sessions exist in the selected time range."
            value={formatDurationSeconds(kpis.avgDurationSec)}
          />
        </PanelGrid>
      )}

      <div className="flex items-start gap-6">
        <AgentRail
          agents={roster}
          selectedAgentId={filter.selectedAgentId}
          onSelectAgent={filter.selectAgent}
          degraded={agentsQuery.isError}
        />

        <PanelCard
          title="Recent Sessions"
          description={
            filter.isFiltered
              ? `Filtered to one agent — ${filter.filteredSessions.length} session${filter.filteredSessions.length === 1 ? '' : 's'}`
              : 'Click a row to view session details'
          }
          className="flex-1 min-w-0"
        >
          <SessionTable
            sessions={filter.filteredSessions}
            isLoading={sessionsQuery.isLoading}
            isError={sessionsQuery.isError}
            isFiltered={filter.isFiltered}
            onRowClick={(id) => navigate(`/sessions/${id}`)}
          />
        </PanelCard>
      </div>
    </div>
  );
}

interface SessionTableProps {
  sessions: SessionRecord[];
  isLoading: boolean;
  isError: boolean;
  isFiltered: boolean;
  onRowClick: (id: string) => void;
}

function SessionTable({
  sessions,
  isLoading,
  isError,
  isFiltered,
  onRowClick,
}: SessionTableProps) {
  if (isLoading) {
    return (
      <div className="space-y-3">
        {Array.from({ length: 5 }).map((_, i) => (
          <div key={i} className="h-10 bg-muted rounded animate-pulse" />
        ))}
      </div>
    );
  }

  if (isError) {
    return (
      <EmptyState
        title="Unable to load sessions"
        description="The session store may not be configured. Sessions are recorded when a PostgreSQL connection is available."
      />
    );
  }

  if (sessions.length === 0) {
    return (
      <EmptyState
        title={isFiltered ? 'No sessions for this agent' : 'No sessions yet'}
        description={
          isFiltered
            ? 'This agent has no sessions in the current time range. Click another tile or "All agents" to reset.'
            : 'Sessions appear here once an agent runs at least one conversation in the selected time range.'
        }
      />
    );
  }

  return (
    <div className="overflow-x-auto">
      <table className="w-full text-sm" data-testid="session-table">
        <thead>
          <tr className="border-b border-border text-left text-xs font-medium text-muted-foreground uppercase tracking-wider">
            <th className="pb-2 pr-4">Agent</th>
            <th className="pb-2 pr-4">Model</th>
            <th className="pb-2 pr-4">Started</th>
            <th className="pb-2 pr-4 text-right">Duration</th>
            <th className="pb-2 pr-4 text-right">Turns</th>
            <th className="pb-2 pr-4 text-right">Tools</th>
            <th className="pb-2 pr-4 text-right">Tokens</th>
            <th className="pb-2 pr-4 text-right">Cost</th>
            <th className="pb-2 pr-4">Status</th>
            <th className="pb-2 w-32">Context</th>
          </tr>
        </thead>
        <tbody>
          {sessions.map((s) => (
            <SessionTableRow key={s.id} session={s} onRowClick={onRowClick} />
          ))}
        </tbody>
      </table>
    </div>
  );
}
