import { usePromQuery } from '@/hooks/usePromQuery';
import { metricCatalog } from '@/config/metricCatalog';
import { KpiCard } from '@/components/panels/KpiCard';
import { PanelCard } from '@/components/panels/PanelCard';
import { PanelGrid } from '@/components/panels/PanelGrid';
import { LoadingSkeleton } from '@/components/panels/LoadingSkeleton';
import { TimeSeriesChart } from '@/components/charts/TimeSeriesChart';
import { PageHeader } from '@/components/primitives/PageHeader';
import { Section } from '@/components/primitives/Section';
import { HBarList } from '@/components/primitives/HBarList';
import { ArcGauge } from '@/components/primitives/ArcGauge';

function latestValue(data: ReturnType<typeof usePromQuery>['data']): number {
  const dp = data?.series[0]?.dataPoints;
  if (!dp || dp.length === 0) return 0;
  return parseFloat(dp[dp.length - 1]!.value) || 0;
}

function formatValue(v: number): string {
  if (v >= 1_000_000) return `${(v / 1_000_000).toFixed(1)}M`;
  if (v >= 1_000) return `${(v / 1_000).toFixed(1)}K`;
  return v.toFixed(0);
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
        <PageHeader title="RAG" subtitle="Retrieval performance and source quality" />
        <PanelGrid columns={4}>
          {Array.from({ length: 4 }).map((_, i) => <LoadingSkeleton key={i} />)}
        </PanelGrid>
      </div>
    );
  }

  const latencyMs = latestValue(avgLatency.data) * 1000;
  const ingestionVal = latestValue(ingestionTotal.data);
  const retrievalVal = latestValue(retrievalTotal.data);
  const total = ingestionVal + retrievalVal;
  const ingestionPct = total > 0 ? (ingestionVal / total) * 100 : 50;
  const retrievalPct = total > 0 ? (retrievalVal / total) * 100 : 50;
  const ratio = ingestionVal > 0 ? (retrievalVal / ingestionVal) * 100 : 0;

  return (
    <div className="space-y-6">
      <PageHeader title="RAG" subtitle="Retrieval performance and source quality" />

      <Section title="Retrieval Metrics" kicker="01">
        <PanelGrid columns={4}>
          <KpiCard
            title="Documents Ingested"
            description="Total documents processed and chunked into the vector store. Shows 0 when the RAG ingestion pipeline has not been triggered or no documents have been indexed."
            value={latestValue(ingestionTotal.data).toFixed(0)}
            sparklineData={ingestionTotal.data?.series[0]?.dataPoints}
          />
          <KpiCard
            title="Retrievals"
            description="Number of retrieval queries executed against the vector and sparse stores. Shows 0 when no agent turns have triggered RAG lookups."
            value={latestValue(retrievalTotal.data).toFixed(0)}
            sparklineData={retrievalTotal.data?.series[0]?.dataPoints}
          />
          <KpiCard
            title="Avg Latency"
            description="Mean time from retrieval query submission to ranked chunk assembly. Shows 0ms when no retrieval operations have been recorded."
            value={`${latencyMs.toFixed(0)}ms`}
            sparklineData={avgLatency.data?.series[0]?.dataPoints}
          />
          <KpiCard
            title="Avg Chunks"
            description="Mean number of document chunks returned per retrieval query after reranking and budget enforcement. Shows 0 when no retrievals have occurred."
            value={latestValue(chunksAvg.data).toFixed(1)}
            sparklineData={chunksAvg.data?.series[0]?.dataPoints}
          />
        </PanelGrid>
      </Section>

      <Section title="Performance" kicker="02">
        <PanelGrid columns={2}>
          <PanelCard title="Ingestion Throughput" description="Documents per minute">
            <TimeSeriesChart series={ingestionRate.data?.series ?? []} unit="docs/min" />
          </PanelCard>
          <PanelCard title="Retrieval Latency" description="Average retrieval time">
            <TimeSeriesChart series={latencyTs.data?.series ?? []} unit="ms" />
          </PanelCard>
        </PanelGrid>
      </Section>

      <Section title="Quality" kicker="03">
        <PanelGrid columns={2}>
          <PanelCard title="Top Sources">
            <HBarList
              items={(bySource.data?.series ?? []).map((s) => ({
                label: s.labels['source'] ?? 'unknown',
                value: parseFloat(s.dataPoints[s.dataPoints.length - 1]?.value ?? '0'),
              }))}
              color="var(--otel-info)"
              formatValue={(v) => v >= 1000 ? `${(v / 1000).toFixed(1)}K` : v.toFixed(0)}
            />
          </PanelCard>
          <PanelCard title="Retrieval Latency">
            <div className="flex items-center justify-center py-4">
              <ArcGauge
                value={latencyMs}
                max={1000}
                size={140}
                color={latencyMs > 500 ? 'var(--otel-warning)' : 'var(--otel-positive)'}
                label={`${latencyMs.toFixed(0)}ms`}
                subtitle="avg retrieval"
                thickness={12}
              />
            </div>
          </PanelCard>
        </PanelGrid>
      </Section>

      <Section title="Pipeline" kicker="04">
        <div className="bg-card border border-border rounded-lg p-4">
          <div className="flex gap-0 h-6 rounded overflow-hidden mb-3">
            <div
              className="h-full bg-otel-info transition-all duration-300"
              style={{ width: `${ingestionPct}%` }}
            />
            <div
              className="h-full bg-otel-positive transition-all duration-300"
              style={{ width: `${retrievalPct}%` }}
            />
          </div>
          <div className="flex gap-6 text-xs font-mono tabular-nums">
            <span className="text-otel-info">&#9679; Ingested: {formatValue(ingestionVal)}</span>
            <span className="text-otel-positive">&#9679; Retrieved: {formatValue(retrievalVal)}</span>
          </div>
          <div className="text-[11px] text-otel-text-dim mt-2">
            Retrieval-to-ingestion ratio: {ratio.toFixed(1)}%
          </div>
        </div>
      </Section>
    </div>
  );
}
