import { useNavigate } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { usePromQuery } from '@/hooks/usePromQuery';
import { metricCatalog } from '@/config/metricCatalog';
import { fetchSessions } from '@/api/sessions';
import { KpiCard } from '@/components/panels/KpiCard';
import { PanelCard } from '@/components/panels/PanelCard';
import { PanelGrid } from '@/components/panels/PanelGrid';
import { LoadingSkeleton } from '@/components/panels/LoadingSkeleton';
import { EmptyState } from '@/components/panels/EmptyState';
import { StatusBadge } from './StatusBadge';
import { formatDuration, formatDurationSeconds, formatTokens, formatCost, formatTimestamp } from './format';

function latestValue(data: ReturnType<typeof usePromQuery>['data']): number {
  const dp = data?.series[0]?.dataPoints;
  if (!dp || dp.length === 0) return 0;
  return parseFloat(dp[dp.length - 1]!.value) || 0;
}

export default function SessionsPage() {
  const navigate = useNavigate();

  const sessionsTotal = usePromQuery(metricCatalog['sessions_total']!.query);
  const sessionsActive = usePromQuery(metricCatalog['sessions_active']!.query);
  const turnsAvg = usePromQuery(metricCatalog['sessions_turns_avg']!.query);
  const durationAvg = usePromQuery(metricCatalog['sessions_duration_avg']!.query);

  const sessionsQuery = useQuery({
    queryKey: ['sessions-list'],
    queryFn: () => fetchSessions(50, 0),
    staleTime: 30_000,
  });

  const isKpiLoading = sessionsTotal.isLoading;

  return (
    <div className="space-y-6">
      <h1 className="text-xl font-bold text-foreground">Session Analytics</h1>

      {isKpiLoading ? (
        <PanelGrid columns={4}>
          {Array.from({ length: 4 }).map((_, i) => <LoadingSkeleton key={i} />)}
        </PanelGrid>
      ) : (
        <PanelGrid columns={4}>
          <KpiCard title="Total Sessions" value={latestValue(sessionsTotal.data).toFixed(0)} sparklineData={sessionsTotal.data?.series[0]?.dataPoints} />
          <KpiCard title="Active Sessions" value={latestValue(sessionsActive.data).toFixed(0)} sparklineData={sessionsActive.data?.series[0]?.dataPoints} />
          <KpiCard title="Avg Turns/Session" value={latestValue(turnsAvg.data).toFixed(1)} sparklineData={turnsAvg.data?.series[0]?.dataPoints} />
          <KpiCard title="Avg Duration" value={formatDurationSeconds(latestValue(durationAvg.data))} sparklineData={durationAvg.data?.series[0]?.dataPoints} />
        </PanelGrid>
      )}

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
      <table className="w-full text-sm">
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
