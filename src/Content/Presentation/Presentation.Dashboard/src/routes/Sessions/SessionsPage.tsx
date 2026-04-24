import { usePromQuery } from '@/hooks/usePromQuery';
import { metricCatalog } from '@/config/metricCatalog';
import { KpiCard } from '@/components/panels/KpiCard';
import { PanelCard } from '@/components/panels/PanelCard';
import { PanelGrid } from '@/components/panels/PanelGrid';
import { LoadingSkeleton } from '@/components/panels/LoadingSkeleton';
import { TimeSeriesChart } from '@/components/charts/TimeSeriesChart';

function latestValue(data: ReturnType<typeof usePromQuery>['data']): number {
  const dp = data?.series[0]?.dataPoints;
  if (!dp || dp.length === 0) return 0;
  return parseFloat(dp[dp.length - 1]!.value) || 0;
}

function formatDuration(seconds: number): string {
  if (seconds < 60) return `${seconds.toFixed(0)}s`;
  if (seconds < 3600) return `${(seconds / 60).toFixed(1)}m`;
  return `${(seconds / 3600).toFixed(1)}h`;
}

export default function SessionsPage() {
  const sessionsTotal = usePromQuery(metricCatalog['sessions_total']!.query);
  const sessionsActive = usePromQuery(metricCatalog['sessions_active']!.query);
  const turnsAvg = usePromQuery(metricCatalog['sessions_turns_avg']!.query);
  const durationAvg = usePromQuery(metricCatalog['sessions_duration_avg']!.query);
  const activeTs = usePromQuery(metricCatalog['sessions_active_ts']!.query);
  const turnsTs = usePromQuery(metricCatalog['sessions_turns_ts']!.query);

  if (sessionsTotal.isLoading) {
    return (
      <div className="space-y-6">
        <h1 className="text-xl font-bold text-foreground">Session Analytics</h1>
        <PanelGrid columns={4}>
          {Array.from({ length: 4 }).map((_, i) => <LoadingSkeleton key={i} />)}
        </PanelGrid>
      </div>
    );
  }

  return (
    <div className="space-y-6">
      <h1 className="text-xl font-bold text-foreground">Session Analytics</h1>

      <PanelGrid columns={4}>
        <KpiCard title="Total Sessions" value={latestValue(sessionsTotal.data).toFixed(0)} sparklineData={sessionsTotal.data?.series[0]?.dataPoints} />
        <KpiCard title="Active Sessions" value={latestValue(sessionsActive.data).toFixed(0)} sparklineData={sessionsActive.data?.series[0]?.dataPoints} />
        <KpiCard title="Avg Turns/Session" value={latestValue(turnsAvg.data).toFixed(1)} sparklineData={turnsAvg.data?.series[0]?.dataPoints} />
        <KpiCard title="Avg Duration" value={formatDuration(latestValue(durationAvg.data))} sparklineData={durationAvg.data?.series[0]?.dataPoints} />
      </PanelGrid>

      <PanelGrid columns={2}>
        <PanelCard title="Active Sessions Over Time" description="Session concurrency">
          <TimeSeriesChart series={activeTs.data?.series ?? []} unit="count" />
        </PanelCard>
        <PanelCard title="Conversation Turns" description="Turns per minute">
          <TimeSeriesChart series={turnsTs.data?.series ?? []} unit="turns/min" />
        </PanelCard>
      </PanelGrid>
    </div>
  );
}
