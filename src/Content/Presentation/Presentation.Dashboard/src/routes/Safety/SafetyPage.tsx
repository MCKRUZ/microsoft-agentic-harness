import { usePromQuery } from '@/hooks/usePromQuery';
import { metricCatalog } from '@/config/metricCatalog';
import { KpiCard } from '@/components/panels/KpiCard';
import { PanelCard } from '@/components/panels/PanelCard';
import { PanelGrid } from '@/components/panels/PanelGrid';
import { LoadingSkeleton } from '@/components/panels/LoadingSkeleton';
import { TimeSeriesChart } from '@/components/charts/TimeSeriesChart';
import { MetricPieChart } from '@/components/charts/PieChart';
import { GaugeChart } from '@/components/charts/GaugeChart';

function latestValue(data: ReturnType<typeof usePromQuery>['data']): number {
  const dp = data?.series[0]?.dataPoints;
  if (!dp || dp.length === 0) return 0;
  return parseFloat(dp[dp.length - 1]!.value) || 0;
}

export default function SafetyPage() {
  const total = usePromQuery(metricCatalog['safety_total']!.query);
  const blocked = usePromQuery(metricCatalog['safety_blocked']!.query);
  const checks = usePromQuery(metricCatalog['safety_checks_total']!.query);
  const violationsTs = usePromQuery(metricCatalog['safety_violations_ts']!.query);
  const byCategory = usePromQuery(metricCatalog['safety_by_category']!.query);
  const blockRate = usePromQuery(metricCatalog['safety_block_rate']!.query);

  if (total.isLoading) {
    return (
      <div className="space-y-6">
        <h1 className="text-xl font-bold text-foreground">Content Safety</h1>
        <PanelGrid columns={3}>
          {Array.from({ length: 3 }).map((_, i) => <LoadingSkeleton key={i} />)}
        </PanelGrid>
      </div>
    );
  }

  return (
    <div className="space-y-6">
      <h1 className="text-xl font-bold text-foreground">Content Safety</h1>

      <PanelGrid columns={3}>
        <KpiCard title="Total Violations" value={latestValue(total.data).toFixed(0)} sparklineData={total.data?.series[0]?.dataPoints} />
        <KpiCard title="Blocked Requests" value={latestValue(blocked.data).toFixed(0)} sparklineData={blocked.data?.series[0]?.dataPoints} />
        <KpiCard title="Safety Checks" value={latestValue(checks.data).toFixed(0)} sparklineData={checks.data?.series[0]?.dataPoints} />
      </PanelGrid>

      <PanelGrid columns={2}>
        <PanelCard title="Violation Trend" description="Violations per minute">
          <TimeSeriesChart series={violationsTs.data?.series ?? []} unit="count/min" />
        </PanelCard>
        <PanelCard title="Violations by Category">
          <MetricPieChart series={byCategory.data?.series ?? []} unit="count" />
        </PanelCard>
      </PanelGrid>

      <PanelCard title="Block Rate">
        <GaugeChart
          value={latestValue(blockRate.data)}
          max={1}
          label="Requests Blocked"
          unit="percent"
          thresholds={{ warn: 0.05, critical: 0.15 }}
        />
      </PanelCard>
    </div>
  );
}
