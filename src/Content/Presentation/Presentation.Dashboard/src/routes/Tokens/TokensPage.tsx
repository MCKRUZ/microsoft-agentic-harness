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

function formatTokens(value: number): string {
  if (value >= 1000000) return `${(value / 1000000).toFixed(1)}M`;
  if (value >= 1000) return `${(value / 1000).toFixed(1)}K`;
  return value.toFixed(0);
}

export default function TokensPage() {
  const inputTokens = usePromQuery(metricCatalog['tokens_input_total']!.query);
  const outputTokens = usePromQuery(metricCatalog['tokens_output_total']!.query);
  const cacheRead = usePromQuery(metricCatalog['tokens_cache_read']!.query);
  const cacheWrite = usePromQuery(metricCatalog['tokens_cache_write']!.query);
  const inputRate = usePromQuery(metricCatalog['tokens_input_rate']!.query);
  const outputRate = usePromQuery(metricCatalog['tokens_output_rate']!.query);
  const byModel = usePromQuery(metricCatalog['tokens_by_model']!.query);
  const cacheHitRate = usePromQuery(metricCatalog['tokens_cache_hit_rate_ts']!.query);

  const anyLoading = inputTokens.isLoading || outputTokens.isLoading;

  if (anyLoading) {
    return (
      <div className="space-y-6">
        <h1 className="text-xl font-bold text-foreground">Token Analytics</h1>
        <PanelGrid columns={4}>
          {Array.from({ length: 4 }).map((_, i) => <LoadingSkeleton key={i} />)}
        </PanelGrid>
      </div>
    );
  }

  return (
    <div className="space-y-6">
      <h1 className="text-xl font-bold text-foreground">Token Analytics</h1>

      <PanelGrid columns={4}>
        <KpiCard title="Input Tokens" value={formatTokens(latestValue(inputTokens.data))} sparklineData={inputTokens.data?.series[0]?.dataPoints} />
        <KpiCard title="Output Tokens" value={formatTokens(latestValue(outputTokens.data))} sparklineData={outputTokens.data?.series[0]?.dataPoints} />
        <KpiCard title="Cache Read" value={formatTokens(latestValue(cacheRead.data))} sparklineData={cacheRead.data?.series[0]?.dataPoints} />
        <KpiCard title="Cache Write" value={formatTokens(latestValue(cacheWrite.data))} sparklineData={cacheWrite.data?.series[0]?.dataPoints} />
      </PanelGrid>

      <PanelGrid columns={2}>
        <PanelCard title="Input Token Rate" description="Tokens per minute">
          <TimeSeriesChart series={inputRate.data?.series ?? []} unit="tokens/min" />
        </PanelCard>
        <PanelCard title="Output Token Rate" description="Tokens per minute">
          <TimeSeriesChart series={outputRate.data?.series ?? []} unit="tokens/min" />
        </PanelCard>
      </PanelGrid>

      <PanelGrid columns={2}>
        <PanelCard title="Distribution by Model">
          <MetricPieChart series={byModel.data?.series ?? []} unit="tokens" />
        </PanelCard>
        <PanelCard title="Cache Hit Rate">
          <GaugeChart
            value={latestValue(cacheHitRate.data)}
            max={1}
            label="Hit Rate"
            unit="percent"
            thresholds={{ warn: 0.3, critical: 0.1 }}
          />
        </PanelCard>
      </PanelGrid>
    </div>
  );
}
