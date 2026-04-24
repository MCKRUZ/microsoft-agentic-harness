import { usePromQuery } from '@/hooks/usePromQuery';
import { metricCatalog } from '@/config/metricCatalog';
import { KpiCard } from '@/components/panels/KpiCard';
import { PanelCard } from '@/components/panels/PanelCard';
import { PanelGrid } from '@/components/panels/PanelGrid';
import { LoadingSkeleton } from '@/components/panels/LoadingSkeleton';
import { TimeSeriesChart } from '@/components/charts/TimeSeriesChart';
import { GaugeChart } from '@/components/charts/GaugeChart';

function useMetric(catalogId: string) {
  const entry = metricCatalog[catalogId]!;
  return { entry, ...usePromQuery(entry.query) };
}

function latestValue(data: ReturnType<typeof usePromQuery>['data']): number {
  const dp = data?.series[0]?.dataPoints;
  if (!dp || dp.length === 0) return 0;
  return parseFloat(dp[dp.length - 1]!.value) || 0;
}

function formatKpi(value: number, unit: string): string {
  if (unit === 'usd') return `$${value.toFixed(4)}`;
  if (unit === 'percent') return `${(value * 100).toFixed(1)}%`;
  if (unit === 'tokens/min') return value >= 1000 ? `${(value / 1000).toFixed(1)}K` : value.toFixed(0);
  if (value >= 1000000) return `${(value / 1000000).toFixed(1)}M`;
  if (value >= 1000) return `${(value / 1000).toFixed(1)}K`;
  return value.toFixed(0);
}

export default function OverviewPage() {
  const tokensPerMin = useMetric('tokens_per_minute');
  const activeSessions = useMetric('active_sessions');
  const costToday = useMetric('cost_today');
  const cacheHitRate = useMetric('cache_hit_rate');
  const safetyViolations = useMetric('safety_violations');
  const budgetStatus = useMetric('budget_status');

  const tokenRate = usePromQuery('rate(agentic_harness_agent_tokens_total_sum[5m]) * 60');
  const costRate = usePromQuery('rate(agentic_harness_agent_tokens_cost_estimated_total[5m]) * 3600');

  const anyLoading = tokensPerMin.isLoading || activeSessions.isLoading || costToday.isLoading || cacheHitRate.isLoading;

  if (anyLoading) {
    return (
      <div className="space-y-6">
        <h1 className="text-xl font-bold text-foreground">Overview</h1>
        <PanelGrid columns={3}>
          {Array.from({ length: 6 }).map((_, i) => <LoadingSkeleton key={i} />)}
        </PanelGrid>
      </div>
    );
  }

  return (
    <div className="space-y-6">
      <h1 className="text-xl font-bold text-foreground">Overview</h1>

      <PanelGrid columns={3}>
        <KpiCard
          title={tokensPerMin.entry.title}
          value={formatKpi(latestValue(tokensPerMin.data), tokensPerMin.entry.unit)}
          unit="tok/min"
          sparklineData={tokensPerMin.data?.series[0]?.dataPoints}
        />
        <KpiCard
          title={activeSessions.entry.title}
          value={formatKpi(latestValue(activeSessions.data), activeSessions.entry.unit)}
          sparklineData={activeSessions.data?.series[0]?.dataPoints}
        />
        <KpiCard
          title={costToday.entry.title}
          value={formatKpi(latestValue(costToday.data), costToday.entry.unit)}
          unit="USD"
          sparklineData={costToday.data?.series[0]?.dataPoints}
        />
        <KpiCard
          title={cacheHitRate.entry.title}
          value={formatKpi(latestValue(cacheHitRate.data), cacheHitRate.entry.unit)}
          sparklineData={cacheHitRate.data?.series[0]?.dataPoints}
        />
        <KpiCard
          title={safetyViolations.entry.title}
          value={formatKpi(latestValue(safetyViolations.data), safetyViolations.entry.unit)}
          sparklineData={safetyViolations.data?.series[0]?.dataPoints}
        />
        <KpiCard
          title={budgetStatus.entry.title}
          value={formatKpi(latestValue(budgetStatus.data), budgetStatus.entry.unit)}
          sparklineData={budgetStatus.data?.series[0]?.dataPoints}
        />
      </PanelGrid>

      <PanelGrid columns={2}>
        <PanelCard title="Token Throughput" description="Tokens consumed per minute">
          <TimeSeriesChart series={tokenRate.data?.series ?? []} unit="tokens/min" />
        </PanelCard>
        <PanelCard title="Cost Rate" description="USD burn rate per hour">
          <TimeSeriesChart series={costRate.data?.series ?? []} unit="usd" />
        </PanelCard>
      </PanelGrid>

      <PanelGrid columns={2}>
        <PanelCard title="Cache Efficiency">
          <GaugeChart
            value={latestValue(cacheHitRate.data)}
            max={1}
            label="Hit Rate"
            unit="percent"
            thresholds={{ warn: 0.3, critical: 0.1 }}
          />
        </PanelCard>
        <PanelCard title="Budget Utilization">
          <GaugeChart
            value={latestValue(budgetStatus.data)}
            max={1}
            label="Used"
            unit="percent"
            thresholds={{ warn: 0.75, critical: 0.9 }}
          />
        </PanelCard>
      </PanelGrid>
    </div>
  );
}
