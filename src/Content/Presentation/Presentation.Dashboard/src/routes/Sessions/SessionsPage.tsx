import { useNavigate } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { usePromQuery } from '@/hooks/usePromQuery';
import { metricCatalog } from '@/config/metricCatalog';
import { fetchSessions } from '@/api/sessions';
import { useTimeRangeStore } from '@/stores/timeRangeStore';
import { useMemo } from 'react';
import { KpiCard } from '@/components/panels/KpiCard';
import { PanelCard } from '@/components/panels/PanelCard';
import { PanelGrid } from '@/components/panels/PanelGrid';
import { LoadingSkeleton } from '@/components/panels/LoadingSkeleton';
import { EmptyState } from '@/components/panels/EmptyState';
import { PageHeader } from '@/components/primitives/PageHeader';
import { StatusBadge } from './StatusBadge';
import { formatDuration, formatDurationSeconds, formatTokens, formatCost, formatTimestamp } from './format';
import type { SessionRecord } from '@/api/types';

function latestValue(data: ReturnType<typeof usePromQuery>['data']): number {
  const dp = data?.series[0]?.dataPoints;
  if (!dp || dp.length === 0) return 0;
  return parseFloat(dp[dp.length - 1]!.value) || 0;
}

function formatTimeLabel(date: Date): string {
  return date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
}

function formatGanttDuration(ms: number | null): string {
  if (ms === null || ms <= 0) return 'active';
  if (ms < 1000) return `${ms}ms`;
  const seconds = ms / 1000;
  if (seconds < 60) return `${seconds.toFixed(1)}s`;
  const minutes = seconds / 60;
  if (minutes < 60) return `${minutes.toFixed(1)}m`;
  const hours = minutes / 60;
  return `${hours.toFixed(1)}h`;
}

function getStatusColors(status: string): { bg: string; border: string; hover: string } {
  switch (status) {
    case 'active':
      return {
        bg: 'bg-otel-positive/30',
        border: 'border-otel-positive',
        hover: 'hover:bg-otel-positive/45',
      };
    case 'errored':
      return {
        bg: 'bg-otel-negative/30',
        border: 'border-otel-negative',
        hover: 'hover:bg-otel-negative/45',
      };
    default:
      return {
        bg: 'bg-otel-accent/25',
        border: 'border-otel-accent',
        hover: 'hover:bg-otel-accent/40',
      };
  }
}

interface GanttTimelineProps {
  sessions: SessionRecord[];
  onSessionClick: (id: string) => void;
}

function GanttTimeline({ sessions, onSessionClick }: GanttTimelineProps) {
  const { rangeStart, rangeEnd, rangeWidth, markers } = useMemo(() => {
    if (sessions.length === 0) {
      const now = Date.now();
      return { rangeStart: now, rangeEnd: now, rangeWidth: 1, markers: [] as Date[] };
    }

    const starts = sessions.map((s) => new Date(s.startedAt).getTime());
    const ends = sessions.map((s) =>
      s.endedAt ? new Date(s.endedAt).getTime() : Date.now(),
    );

    const minStart = Math.min(...starts);
    const maxEnd = Math.max(...ends);
    const width = maxEnd - minStart || 1;

    const m = [0, 0.25, 0.5, 0.75, 1].map(
      (pct) => new Date(minStart + width * pct),
    );

    return { rangeStart: minStart, rangeEnd: maxEnd, rangeWidth: width, markers: m };
  }, [sessions]);

  if (sessions.length === 0) {
    return (
      <EmptyState
        title="No sessions to visualize"
        description="Sessions will appear on the timeline once agent conversations are recorded."
      />
    );
  }

  return (
    <div className="space-y-1">
      {/* Time axis */}
      <div className="relative h-6 mb-2 border-b border-border/50">
        {markers.map((m, i) => {
          const pct = ((m.getTime() - rangeStart) / rangeWidth) * 100;
          return (
            <span
              key={i}
              className="absolute text-[10px] text-muted-foreground -translate-x-1/2 top-0"
              style={{ left: `${pct}%` }}
            >
              {formatTimeLabel(m)}
            </span>
          );
        })}
      </div>

      {/* Session bars */}
      <div className="space-y-1.5">
        {sessions.map((s) => {
          const startMs = new Date(s.startedAt).getTime();
          const endMs = s.endedAt ? new Date(s.endedAt).getTime() : Date.now();
          const duration = endMs - startMs;

          const leftPct = ((startMs - rangeStart) / rangeWidth) * 100;
          const widthPct = Math.max((duration / rangeWidth) * 100, 2);

          const colors = getStatusColors(s.status);

          return (
            <div key={s.id} className="relative h-8" title={`${s.agentName} - ${formatGanttDuration(s.durationMs)}`}>
              <button
                onClick={() => onSessionClick(s.id)}
                className={`absolute top-0 h-full rounded-md border ${colors.bg} ${colors.border} ${colors.hover} transition-colors cursor-pointer flex items-center overflow-hidden px-2 gap-1.5`}
                style={{ left: `${leftPct}%`, width: `${widthPct}%`, minWidth: '60px' }}
              >
                <span className="text-xs font-medium text-foreground truncate">
                  {s.agentName}
                </span>
                <span className="text-[10px] text-muted-foreground whitespace-nowrap ml-auto">
                  {formatGanttDuration(s.durationMs)}
                </span>
              </button>
            </div>
          );
        })}
      </div>
    </div>
  );
}

export default function SessionsPage() {
  const navigate = useNavigate();
  const preset = useTimeRangeStore((s) => s.preset);
  const customStart = useTimeRangeStore((s) => s.customStart);
  const customEnd = useTimeRangeStore((s) => s.customEnd);
  const getRange = useTimeRangeStore((s) => s.getRange);
  const { start, end } = useMemo(() => getRange(), [preset, customStart, customEnd, getRange]);

  const sessionsTotal = usePromQuery(metricCatalog['sessions_total']!.query);
  const sessionsActive = usePromQuery(metricCatalog['sessions_active']!.query);
  const turnsAvg = usePromQuery(metricCatalog['sessions_turns_avg']!.query);
  const durationAvg = usePromQuery(metricCatalog['sessions_duration_avg']!.query);

  const sessionsQuery = useQuery({
    queryKey: ['sessions-list', start, end],
    queryFn: () => fetchSessions(50, 0, undefined, start, end),
    staleTime: 30_000,
  });

  const isKpiLoading = sessionsTotal.isLoading;

  return (
    <div className="space-y-6">
      <PageHeader title="Sessions" subtitle="Session analytics and timeline view" />

      {isKpiLoading ? (
        <PanelGrid columns={4}>
          {Array.from({ length: 4 }).map((_, i) => <LoadingSkeleton key={i} />)}
        </PanelGrid>
      ) : (
        <PanelGrid columns={4}>
          <KpiCard
            title="Total Sessions"
            description="Number of agent conversation sessions started in the selected time range. Shows 0 when no conversations have been initiated."
            value={latestValue(sessionsTotal.data).toFixed(0)}
            sparklineData={sessionsTotal.data?.series[0]?.dataPoints}
          />
          <KpiCard
            title="Active Sessions"
            description="Sessions currently in progress with an open connection. Shows 0 when all sessions have completed or no agents are running."
            value={latestValue(sessionsActive.data).toFixed(0)}
            sparklineData={sessionsActive.data?.series[0]?.dataPoints}
          />
          <KpiCard
            title="Avg Turns/Session"
            description="Mean number of user-agent exchange turns per session. Shows 0 when no completed sessions exist in the selected time range."
            value={latestValue(turnsAvg.data).toFixed(1)}
            sparklineData={turnsAvg.data?.series[0]?.dataPoints}
          />
          <KpiCard
            title="Avg Duration"
            description="Mean wall-clock time from session start to session end. Shows 0 when no completed sessions exist in the selected time range."
            value={formatDurationSeconds(latestValue(durationAvg.data))}
            sparklineData={durationAvg.data?.series[0]?.dataPoints}
          />
        </PanelGrid>
      )}

      <PanelCard title="Session Timeline" description="Click a session bar to view details">
        <GanttTimeline
          sessions={sessionsQuery.data ?? []}
          onSessionClick={(id) => navigate(`/sessions/${id}`)}
        />
      </PanelCard>

      <PanelCard title="Recent Sessions" description="Click a row to view session details">
        <SessionTable
          sessions={sessionsQuery.data ?? []}
          isLoading={sessionsQuery.isLoading}
          isError={sessionsQuery.isError}
          onRowClick={(id) => navigate(`/sessions/${id}`)}
        />
      </PanelCard>
    </div>
  );
}

interface SessionTableProps {
  sessions: Awaited<ReturnType<typeof fetchSessions>>;
  isLoading: boolean;
  isError: boolean;
  onRowClick: (id: string) => void;
}

function SessionTable({ sessions, isLoading, isError, onRowClick }: SessionTableProps) {
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
        title="No sessions recorded"
        description="Sessions will appear here once agent conversations are initiated."
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
            <th className="pb-2">Status</th>
          </tr>
        </thead>
        <tbody>
          {sessions.map((s) => (
            <tr
              key={s.id}
              data-testid={`session-row-${s.id}`}
              onClick={() => onRowClick(s.id)}
              className="border-b border-border/50 cursor-pointer hover:bg-muted/50 transition-colors"
            >
              <td className="py-2.5 pr-4 font-medium text-card-foreground">{s.agentName}</td>
              <td className="py-2.5 pr-4 text-muted-foreground">{s.model ?? '--'}</td>
              <td className="py-2.5 pr-4 text-muted-foreground">{formatTimestamp(s.startedAt)}</td>
              <td className="py-2.5 pr-4 text-right text-muted-foreground">{formatDuration(s.durationMs)}</td>
              <td className="py-2.5 pr-4 text-right">{s.turnCount}</td>
              <td className="py-2.5 pr-4 text-right">{s.toolCallCount}</td>
              <td className="py-2.5 pr-4 text-right text-muted-foreground">
                {formatTokens(s.totalInputTokens)} / {formatTokens(s.totalOutputTokens)}
              </td>
              <td className="py-2.5 pr-4 text-right">{formatCost(s.totalCostUsd)}</td>
              <td className="py-2.5"><StatusBadge status={s.status} /></td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
