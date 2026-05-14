import { usePromQuery } from '@/hooks/usePromQuery';
import { KpiCard } from '@/components/panels/KpiCard';
import { PanelCard } from '@/components/panels/PanelCard';
import { PanelGrid } from '@/components/panels/PanelGrid';
import { LoadingSkeleton } from '@/components/panels/LoadingSkeleton';
import { GaugeChart } from '@/components/charts/GaugeChart';
import { HBarList } from '@/components/charts/HBarList';
import { StatusDot } from '@/components/primitives/StatusDot';
import { latestValue, formatKpi, seriesToBars } from './pulse-helpers';

const TOOL_ERROR_RATE =
  'sum(agentic_harness_agent_tool_errors_total) / (sum(agentic_harness_agent_tool_invocations_total) > 0) or vector(0)';
const SAFETY_BLOCK_RATE =
  'sum(agentic_harness_agent_safety_blocks_total) / (sum(agentic_harness_agent_safety_evaluations_total) > 0) or vector(0)';
const RAG_HIT_RATE =
  'sum(agentic_harness_rag_retrieval_hits_total) / (sum(agentic_harness_rag_retrieval_queries_total) > 0) or vector(0)';
const GROUNDING =
  'agentic_harness_rag_grounding_score_sum / agentic_harness_rag_grounding_score_count or vector(0)';
const TOOLS_CALLS =
  'sum by (agent_tool_name) (agentic_harness_agent_tool_invocations_total)';
const TOOLS_ERRORS =
  'sum by (agent_tool_name) (agentic_harness_agent_tool_errors_total)';
const TOOLS_P95 =
  'histogram_quantile(0.95, sum by (le, agent_tool_name) (agentic_harness_agent_tool_duration_bucket))';
const SAFETY_BLOCKS =
  'sum(agentic_harness_agent_safety_blocks_total) or vector(0)';
const SAFETY_FLAGS =
  'sum(agentic_harness_agent_safety_flags_total) or vector(0)';
const SAFETY_REDACTIONS =
  'sum(agentic_harness_agent_safety_redactions_total) or vector(0)';
const RAG_SOURCES =
  'sum by (source) (agentic_harness_rag_source_retrievals_total)';

export function QualityTab() {
  const toolErrorRate = usePromQuery(TOOL_ERROR_RATE);
  const safetyBlockRate = usePromQuery(SAFETY_BLOCK_RATE);
  const ragHitRate = usePromQuery(RAG_HIT_RATE);
  const groundingScore = usePromQuery(GROUNDING);
  const toolsCalls = usePromQuery(TOOLS_CALLS);
  const toolsErrors = usePromQuery(TOOLS_ERRORS);
  const toolsP95 = usePromQuery(TOOLS_P95);
  const safetyBlocks = usePromQuery(SAFETY_BLOCKS);
  const safetyFlags = usePromQuery(SAFETY_FLAGS);
  const safetyRedactions = usePromQuery(SAFETY_REDACTIONS);
  const ragSources = usePromQuery(RAG_SOURCES);

  const anyLoading =
    toolErrorRate.isLoading ||
    safetyBlockRate.isLoading ||
    ragHitRate.isLoading ||
    groundingScore.isLoading;

  if (anyLoading) {
    return (
      <div className="space-y-4">
        <PanelGrid columns={4}>
          {Array.from({ length: 4 }).map((_, i) => (
            <LoadingSkeleton key={i} />
          ))}
        </PanelGrid>
        <LoadingSkeleton className="h-48" />
      </div>
    );
  }

  const toolErrVal = Math.min(latestValue(toolErrorRate.data), 1);
  const safetyBlockVal = Math.min(latestValue(safetyBlockRate.data), 1);
  const ragHitVal = Math.min(latestValue(ragHitRate.data), 1);
  const groundingVal = Math.min(latestValue(groundingScore.data), 1);

  const toolCallsSeries = toolsCalls.data?.series ?? [];
  const toolErrorsSeries = toolsErrors.data?.series ?? [];
  const toolP95Series = toolsP95.data?.series ?? [];

  const toolRows = toolCallsSeries
    .map((s) => {
      const name = s.labels['agent_tool_name'] ?? 'unknown';
      const calls =
        parseFloat(s.dataPoints[s.dataPoints.length - 1]?.value ?? '0') || 0;
      const errS = toolErrorsSeries.find(
        (e) => e.labels['agent_tool_name'] === name,
      );
      const errors = errS
        ? parseFloat(errS.dataPoints[errS.dataPoints.length - 1]?.value ?? '0') || 0
        : 0;
      const p95S = toolP95Series.find(
        (e) => e.labels['agent_tool_name'] === name,
      );
      const p95 = p95S
        ? parseFloat(p95S.dataPoints[p95S.dataPoints.length - 1]?.value ?? '0') || 0
        : 0;
      const errPct = calls > 0 ? errors / calls : 0;
      return { name, calls, errors, errPct, p95 };
    })
    .sort((a, b) => b.calls - a.calls);

  const blocksVal = latestValue(safetyBlocks.data);
  const flagsVal = latestValue(safetyFlags.data);
  const redactionsVal = latestValue(safetyRedactions.data);

  const ragSourceBars = seriesToBars(
    ragSources.data?.series ?? [],
    'source',
    (v) => v.toFixed(0),
  );

  return (
    <div className="space-y-6">
      <PanelGrid columns={4}>
        <KpiCard
          title="Tool Error Rate"
          value={formatKpi(toolErrVal, 'percent')}
          subtitle={toolErrVal > 0.05 ? 'above threshold' : 'within target'}
        />
        <KpiCard
          title="Safety Block Rate"
          value={formatKpi(safetyBlockVal, 'percent')}
        />
        <KpiCard
          title="RAG Hit Rate"
          value={formatKpi(ragHitVal, 'percent')}
          subtitle="queries with relevant results"
        />
        <KpiCard
          title="Grounding Score"
          value={formatKpi(groundingVal, 'percent')}
          subtitle="turns citing retrieved chunks"
        />
      </PanelGrid>

      {/* Tool reliability + Safety card */}
      <div className="grid grid-cols-1 lg:grid-cols-[2fr_1fr] gap-4">
        <PanelCard
          title="Tool reliability"
          description="sorted by call volume"
        >
          {toolRows.length === 0 ? (
            <p className="text-xs text-otel-text-mute py-6 text-center">
              No tool data yet
            </p>
          ) : (
            <div className="overflow-x-auto">
              <table className="w-full text-xs">
                <thead>
                  <tr className="border-b border-border text-otel-text-dim">
                    <th className="text-left py-2 font-medium">Tool</th>
                    <th className="text-right py-2 font-medium">Calls</th>
                    <th className="text-right py-2 font-medium">Err %</th>
                    <th className="text-right py-2 font-medium">p95</th>
                    <th className="text-right py-2 font-medium w-12">
                      Health
                    </th>
                  </tr>
                </thead>
                <tbody>
                  {toolRows.slice(0, 10).map((row) => (
                    <tr
                      key={row.name}
                      className="border-b border-border/30"
                    >
                      <td className="py-2 font-mono text-card-foreground">
                        {row.name}
                      </td>
                      <td className="py-2 text-right tabular-nums">
                        {row.calls.toFixed(0)}
                      </td>
                      <td className="py-2 text-right tabular-nums">
                        {(row.errPct * 100).toFixed(1)}%
                      </td>
                      <td className="py-2 text-right tabular-nums">
                        {row.p95.toFixed(0)} ms
                      </td>
                      <td className="py-2 text-right">
                        <StatusDot
                          status={
                            row.errPct > 0.1
                              ? 'critical'
                              : row.errPct > 0.05
                                ? 'warning'
                                : 'ok'
                          }
                        />
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </PanelCard>

        <PanelCard title="Safety" description="block rate + counts">
          <GaugeChart
            value={safetyBlockVal}
            max={1}
            label="Block Rate"
            unit="percent"
            thresholds={{ warn: 0.05, critical: 0.1 }}
          />
          <div className="grid grid-cols-3 gap-2 mt-4">
            <div className="text-center">
              <div className="text-lg font-bold text-card-foreground">
                {blocksVal.toFixed(0)}
              </div>
              <div className="text-[10px] text-otel-text-mute uppercase">
                Blocks
              </div>
            </div>
            <div className="text-center">
              <div className="text-lg font-bold text-card-foreground">
                {flagsVal.toFixed(0)}
              </div>
              <div className="text-[10px] text-otel-text-mute uppercase">
                Flags
              </div>
            </div>
            <div className="text-center">
              <div className="text-lg font-bold text-card-foreground">
                {redactionsVal.toFixed(0)}
              </div>
              <div className="text-[10px] text-otel-text-mute uppercase">
                Redactions
              </div>
            </div>
          </div>
        </PanelCard>
      </div>

      {/* RAG sources */}
      <PanelCard
        title="RAG sources"
        description="query volume per source"
      >
        {ragSourceBars.length === 0 ? (
          <p className="text-xs text-otel-text-mute py-6 text-center">
            No RAG source data yet
          </p>
        ) : (
          <HBarList items={ragSourceBars} />
        )}
      </PanelCard>
    </div>
  );
}
