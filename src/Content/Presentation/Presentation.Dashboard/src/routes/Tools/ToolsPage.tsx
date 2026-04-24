import { usePromQuery } from '@/hooks/usePromQuery';
import { metricCatalog } from '@/config/metricCatalog';
import { KpiCard } from '@/components/panels/KpiCard';
import { PanelCard } from '@/components/panels/PanelCard';
import { PanelGrid } from '@/components/panels/PanelGrid';
import { LoadingSkeleton } from '@/components/panels/LoadingSkeleton';
import { TimeSeriesChart } from '@/components/charts/TimeSeriesChart';
import { MetricBarChart } from '@/components/charts/BarChart';

function latestValue(data: ReturnType<typeof usePromQuery>['data']): number {
  const dp = data?.series[0]?.dataPoints;
  if (!dp || dp.length === 0) return 0;
  return parseFloat(dp[dp.length - 1]!.value) || 0;
}

export default function ToolsPage() {
  const callsTotal = usePromQuery(metricCatalog['tools_calls_total']!.query);
  const errorsTotal = usePromQuery(metricCatalog['tools_errors_total']!.query);
  const avgLatency = usePromQuery(metricCatalog['tools_avg_latency']!.query);
  const usefulness = usePromQuery(metricCatalog['tools_usefulness_avg']!.query);
  const callsByTool = usePromQuery(metricCatalog['tools_calls_by_tool']!.query);
  const latencyByTool = usePromQuery(metricCatalog['tools_latency_by_tool']!.query);
  const errorRate = usePromQuery(metricCatalog['tools_error_rate']!.query);

  if (callsTotal.isLoading) {
    return (
      <div className="space-y-6">
        <h1 className="text-xl font-bold text-foreground">Tool Analytics</h1>
        <PanelGrid columns={4}>
          {Array.from({ length: 4 }).map((_, i) => <LoadingSkeleton key={i} />)}
        </PanelGrid>
      </div>
    );
  }

  const latencyMs = latestValue(avgLatency.data) * 1000;

  return (
    <div className="space-y-6">
      <h1 className="text-xl font-bold text-foreground">Tool Analytics</h1>

      <PanelGrid columns={4}>
        <KpiCard title="Total Calls" value={latestValue(callsTotal.data).toFixed(0)} sparklineData={callsTotal.data?.series[0]?.dataPoints} />
        <KpiCard title="Errors" value={latestValue(errorsTotal.data).toFixed(0)} sparklineData={errorsTotal.data?.series[0]?.dataPoints} />
        <KpiCard title="Avg Latency" value={`${latencyMs.toFixed(0)}ms`} sparklineData={avgLatency.data?.series[0]?.dataPoints} />
        <KpiCard title="Usefulness" value={latestValue(usefulness.data).toFixed(2)} unit="/1.0" sparklineData={usefulness.data?.series[0]?.dataPoints} />
      </PanelGrid>

      <PanelGrid columns={2}>
        <PanelCard title="Calls by Tool" description="Invocation count per tool">
          <MetricBarChart series={callsByTool.data?.series ?? []} unit="count" layout="vertical" />
        </PanelCard>
        <PanelCard title="Latency by Tool" description="Average execution time">
          <MetricBarChart series={latencyByTool.data?.series ?? []} unit="ms" layout="vertical" />
        </PanelCard>
      </PanelGrid>

      <PanelCard title="Error Rate Over Time" description="Tool error ratio trend">
        <TimeSeriesChart series={errorRate.data?.series ?? []} unit="percent" />
      </PanelCard>
    </div>
  );
}
