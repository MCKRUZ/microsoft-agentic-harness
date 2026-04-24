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

export default function RagPage() {
  const ingestionTotal = usePromQuery(metricCatalog['rag_ingestion_total']!.query);
  const retrievalTotal = usePromQuery(metricCatalog['rag_retrieval_total']!.query);
  const avgLatency = usePromQuery(metricCatalog['rag_avg_latency']!.query);
  const chunksAvg = usePromQuery(metricCatalog['rag_chunks_avg']!.query);
  const ingestionRate = usePromQuery(metricCatalog['rag_ingestion_rate']!.query);
  const latencyTs = usePromQuery(metricCatalog['rag_retrieval_latency_ts']!.query);
  const bySource = usePromQuery(metricCatalog['rag_by_source']!.query);

  if (ingestionTotal.isLoading) {
    return (
      <div className="space-y-6">
        <h1 className="text-xl font-bold text-foreground">RAG Analytics</h1>
        <PanelGrid columns={4}>
          {Array.from({ length: 4 }).map((_, i) => <LoadingSkeleton key={i} />)}
        </PanelGrid>
      </div>
    );
  }

  const latencyMs = latestValue(avgLatency.data) * 1000;

  return (
    <div className="space-y-6">
      <h1 className="text-xl font-bold text-foreground">RAG Analytics</h1>

      <PanelGrid columns={4}>
        <KpiCard title="Documents Ingested" value={latestValue(ingestionTotal.data).toFixed(0)} sparklineData={ingestionTotal.data?.series[0]?.dataPoints} />
        <KpiCard title="Retrievals" value={latestValue(retrievalTotal.data).toFixed(0)} sparklineData={retrievalTotal.data?.series[0]?.dataPoints} />
        <KpiCard title="Avg Latency" value={`${latencyMs.toFixed(0)}ms`} sparklineData={avgLatency.data?.series[0]?.dataPoints} />
        <KpiCard title="Avg Chunks" value={latestValue(chunksAvg.data).toFixed(1)} sparklineData={chunksAvg.data?.series[0]?.dataPoints} />
      </PanelGrid>

      <PanelGrid columns={2}>
        <PanelCard title="Ingestion Throughput" description="Documents per minute">
          <TimeSeriesChart series={ingestionRate.data?.series ?? []} unit="docs/min" />
        </PanelCard>
        <PanelCard title="Retrieval Latency" description="Average retrieval time">
          <TimeSeriesChart series={latencyTs.data?.series ?? []} unit="ms" />
        </PanelCard>
      </PanelGrid>

      <PanelCard title="Top Sources" description="Most retrieved document sources">
        <MetricBarChart series={bySource.data?.series ?? []} unit="count" layout="vertical" />
      </PanelCard>
    </div>
  );
}
