import { usePromQuery } from '@/hooks/usePromQuery';
import { metricCatalog } from '@/config/metricCatalog';
import { costRateQuery } from '@/config/costQueries';
import { KpiCard } from '@/components/panels/KpiCard';
import { PanelCard } from '@/components/panels/PanelCard';
import { PanelGrid } from '@/components/panels/PanelGrid';
import { LoadingSkeleton } from '@/components/panels/LoadingSkeleton';
import { TimeSeriesChart } from '@/components/charts/TimeSeriesChart';
import { MetricPanel } from '@/components/metrics/MetricPanel';
import { budgetStatusFromUtilization } from '@/lib/metricStatus';

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
  const costRate = usePromQuery(costRateQuery);

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
          description="Rate of total token consumption across all sessions over a 5-minute rolling window."
          value={formatKpi(latestValue(tokensPerMin.data), tokensPerMin.entry.unit)}
          unit="tok/min"
          sparklineData={tokensPerMin.data?.series[0]?.dataPoints}
        />
        <KpiCard
          title={activeSessions.entry.title}
          description="Number of agent sessions currently open and accepting messages."
          value={formatKpi(latestValue(activeSessions.data), activeSessions.entry.unit)}
          sparklineData={activeSessions.data?.series[0]?.dataPoints}
        />
        <KpiCard
          title={costToday.entry.title}
          description="Cumulative estimated LLM spend since midnight UTC based on token counts and model pricing."
          value={formatKpi(latestValue(costToday.data), costToday.entry.unit)}
          unit="USD"
          sparklineData={costToday.data?.series[0]?.dataPoints}
        />
        <KpiCard
          title={cacheHitRate.entry.title}
          description="Ratio of tokens served from prompt cache vs. fresh computation. Higher is better — reduces cost and latency."
          value={formatKpi(latestValue(cacheHitRate.data), cacheHitRate.entry.unit)}
          sparklineData={cacheHitRate.data?.series[0]?.dataPoints}
        />
        <KpiCard
          title={safetyViolations.entry.title}
          description="Total content safety evaluations performed including PII detection, prompt shield, and keyword filters."
          value={formatKpi(latestValue(safetyViolations.data), safetyViolations.entry.unit)}
          sparklineData={safetyViolations.data?.series[0]?.dataPoints}
        />
        <KpiCard
          title={budgetStatus.entry.title}
          description="Current budget utilization as a percentage of the configured daily spending limit."
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
        <MetricPanel
          title="Cache Efficiency"
          value={`${(latestValue(cacheHitRate.data) * 100).toFixed(0)}%`}
          description="hit rate — higher is better"
          status={
            latestValue(cacheHitRate.data) < 0.1
              ? 'critical'
              : latestValue(cacheHitRate.data) < 0.3
                ? 'warning'
                : 'ok'
          }
          sparklineData={cacheHitRate.data?.series[0]?.dataPoints}
        />
        <MetricPanel
          title="Budget Utilization"
          value={`${(latestValue(budgetStatus.data) * 100).toFixed(0)}%`}
          description="of daily cap used"
          status={budgetStatusFromUtilization(latestValue(budgetStatus.data))}
          sparklineData={budgetStatus.data?.series[0]?.dataPoints}
        />
      </PanelGrid>
    </div>
  );
}
