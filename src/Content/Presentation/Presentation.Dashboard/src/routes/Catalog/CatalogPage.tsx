import { useState } from 'react';
import { PageHeader } from '@/components/primitives/PageHeader';
import { TabNav } from '@/components/primitives/TabNav';
import { PanelGrid } from '@/components/panels/PanelGrid';
import { EmptyState } from '@/components/panels/EmptyState';
import { LoadingSkeleton } from '@/components/panels/LoadingSkeleton';
import { usePromQuery } from '@/hooks/usePromQuery';
import { Pill } from '@/components/primitives/Pill';
import type { MetricSeries } from '@/api/types';

const tabs = [
  { label: 'Models', description: 'LLM model usage and performance' },
  { label: 'Agents', description: 'Registered agent configurations' },
  { label: 'Tools', description: 'Available tool definitions' },
];

function latestValue(series: MetricSeries): number {
  const pts = series.dataPoints;
  if (pts.length === 0) return 0;
  return parseFloat(pts[pts.length - 1].value) || 0;
}

function fmt(n: number): string {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`;
  if (n >= 1_000) return `${(n / 1_000).toFixed(1)}K`;
  return n.toFixed(0);
}

function fmtMs(n: number): string {
  if (n >= 1_000) return `${(n / 1_000).toFixed(2)}s`;
  return `${n.toFixed(0)}ms`;
}

function fmtCost(n: number): string {
  return `$${n.toFixed(4)}`;
}

function fmtPct(n: number): string {
  return `${(n * 100).toFixed(1)}%`;
}

// ---------------------------------------------------------------------------
// Models Tab
// ---------------------------------------------------------------------------

function ModelsTab() {
  const tokensIn = usePromQuery('sum by (model) (agentic_harness_agent_tokens_total_sum{direction="input"})');
  const tokensOut = usePromQuery('sum by (model) (agentic_harness_agent_tokens_total_sum{direction="output"})');
  const cost = usePromQuery('sum by (model) (agentic_harness_agent_cost_total_sum)');
  const latency = usePromQuery('histogram_quantile(0.95, sum by (model, le) (rate(agentic_harness_agent_latency_seconds_bucket[5m])))');

  const isLoading = tokensIn.isLoading || tokensOut.isLoading || cost.isLoading || latency.isLoading;

  if (isLoading) {
    return (
      <PanelGrid columns={3}>
        {[1, 2, 3].map((i) => <LoadingSkeleton key={i} />)}
      </PanelGrid>
    );
  }

  const inSeries = tokensIn.data?.series ?? [];
  const outSeries = tokensOut.data?.series ?? [];
  const costSeries = cost.data?.series ?? [];
  const latSeries = latency.data?.series ?? [];

  // Collect all unique model names across queries
  const modelNames = new Set<string>();
  for (const s of [...inSeries, ...outSeries, ...costSeries, ...latSeries]) {
    const name = s.labels['model'];
    if (name) modelNames.add(name);
  }

  if (modelNames.size === 0) {
    // Fallback: try tokensIn without direction filter
    const fallback = tokensIn.data?.series ?? [];
    if (fallback.length === 0) {
      return (
        <EmptyState
          title="No model data"
          description="Prometheus is not returning model metrics. Run an agent session to generate data."
        />
      );
    }
  }

  const findSeries = (list: MetricSeries[], model: string) =>
    list.find((s) => s.labels['model'] === model);

  // If we got no models from the direction-filtered queries, also pull from the
  // unfiltered token total so we at least show model cards
  if (modelNames.size === 0) {
    for (const s of inSeries) {
      const name = s.labels['model'];
      if (name) modelNames.add(name);
    }
  }

  if (modelNames.size === 0) {
    return (
      <EmptyState
        title="No model data"
        description="Prometheus is not returning model metrics. Run an agent session to generate data."
      />
    );
  }

  return (
    <PanelGrid columns={3}>
      {[...modelNames].sort().map((model) => {
        const tIn = findSeries(inSeries, model);
        const tOut = findSeries(outSeries, model);
        const c = findSeries(costSeries, model);
        const l = findSeries(latSeries, model);

        return (
          <div
            key={model}
            className="bg-card border border-border rounded-lg p-4 space-y-3"
          >
            <div className="flex items-center justify-between">
              <h3 className="text-sm font-semibold text-card-foreground truncate" title={model}>
                {model}
              </h3>
              <Pill variant="info">model</Pill>
            </div>

            <div className="grid grid-cols-2 gap-3">
              <MiniKpi label="Tokens In" value={fmt(tIn ? latestValue(tIn) : 0)} />
              <MiniKpi label="Tokens Out" value={fmt(tOut ? latestValue(tOut) : 0)} />
              <MiniKpi label="Cost" value={fmtCost(c ? latestValue(c) : 0)} />
              <MiniKpi label="p95 Latency" value={l ? fmtMs(latestValue(l) * 1000) : '--'} />
            </div>
          </div>
        );
      })}
    </PanelGrid>
  );
}

function MiniKpi({ label, value }: { label: string; value: string }) {
  return (
    <div className="bg-muted/40 rounded px-2.5 py-1.5">
      <div className="text-[10px] text-otel-text-mute uppercase tracking-wider">{label}</div>
      <div className="text-sm font-mono tabular-nums text-card-foreground mt-0.5">{value}</div>
    </div>
  );
}

// ---------------------------------------------------------------------------
// Agents Tab
// ---------------------------------------------------------------------------

function AgentsTab() {
  const sessions = usePromQuery('sum by (agent_name) (agentic_harness_agent_sessions_total)');
  const costByAgent = usePromQuery('sum by (agent_name) (agentic_harness_agent_cost_total_sum)');
  const errorsByAgent = usePromQuery('sum by (agent_name) (agentic_harness_agent_errors_total)');
  const modelByAgent = usePromQuery('sum by (agent_name, model) (agentic_harness_agent_sessions_total)');

  const isLoading = sessions.isLoading || costByAgent.isLoading;

  if (isLoading) {
    return (
      <div className="space-y-2">
        {[1, 2, 3].map((i) => <LoadingSkeleton key={i} className="h-12" />)}
      </div>
    );
  }

  const sessionSeries = sessions.data?.series ?? [];
  const costSeries = costByAgent.data?.series ?? [];
  const errorSeries = errorsByAgent.data?.series ?? [];
  const modelSeries = modelByAgent.data?.series ?? [];

  if (sessionSeries.length === 0) {
    return (
      <EmptyState
        title="No agent data"
        description="No agent sessions recorded yet. Start an agent conversation to generate metrics."
      />
    );
  }

  const agentNames = [...new Set(sessionSeries.map((s) => s.labels['agent_name']))].filter(Boolean).sort();

  const findByAgent = (list: MetricSeries[], name: string) =>
    list.find((s) => s.labels['agent_name'] === name);

  const getModel = (name: string): string => {
    const match = modelSeries.find((s) => s.labels['agent_name'] === name);
    return match?.labels['model'] ?? '--';
  };

  return (
    <div className="space-y-0">
      {/* Header row */}
      <div className="flex items-center gap-4 px-4 py-2 text-[10px] uppercase tracking-wider text-otel-text-mute border-b border-border">
        <div className="flex-[2]">Agent Name</div>
        <div className="flex-[2]">Model</div>
        <div className="flex-1 text-right">Sessions</div>
        <div className="flex-1 text-right">Cost</div>
        <div className="flex-1 text-right">Errors</div>
        <div className="w-20 text-center">Status</div>
      </div>

      {agentNames.map((name) => {
        const sess = findByAgent(sessionSeries, name);
        const c = findByAgent(costSeries, name);
        const err = findByAgent(errorSeries, name);
        const sessCount = sess ? latestValue(sess) : 0;
        const errCount = err ? latestValue(err) : 0;
        const errRate = sessCount > 0 ? errCount / sessCount : 0;
        const isHealthy = errRate < 0.1;

        return (
          <div
            key={name}
            className="flex items-center gap-4 px-4 py-3 border-b border-border/50 hover:bg-muted/30 transition-colors"
          >
            <div className="flex-[2] text-sm font-mono text-card-foreground truncate" title={name}>
              {name}
            </div>
            <div className="flex-[2] text-xs text-otel-text-dim font-mono truncate" title={getModel(name)}>
              {getModel(name)}
            </div>
            <div className="flex-1 text-right text-sm font-mono tabular-nums text-card-foreground">
              {fmt(sessCount)}
            </div>
            <div className="flex-1 text-right text-sm font-mono tabular-nums text-card-foreground">
              {fmtCost(c ? latestValue(c) : 0)}
            </div>
            <div className="flex-1 text-right text-sm font-mono tabular-nums text-card-foreground">
              {errCount > 0 ? (
                <span className="text-otel-negative">{fmt(errCount)} ({fmtPct(errRate)})</span>
              ) : (
                <span className="text-otel-text-mute">0</span>
              )}
            </div>
            <div className="w-20 text-center">
              <Pill variant={isHealthy ? 'positive' : 'negative'}>
                {isHealthy ? 'healthy' : 'degraded'}
              </Pill>
            </div>
          </div>
        );
      })}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Tools Tab
// ---------------------------------------------------------------------------

function ToolsTab() {
  const calls = usePromQuery('sum by (tool_name) (agentic_harness_agent_tool_invocations_total)');
  const errors = usePromQuery('sum by (tool_name) (agentic_harness_agent_tool_errors_total)');
  const avgLat = usePromQuery('sum by (tool_name) (rate(agentic_harness_agent_tool_duration_seconds_sum[5m])) / sum by (tool_name) (rate(agentic_harness_agent_tool_duration_seconds_count[5m]))');
  const p95Lat = usePromQuery('histogram_quantile(0.95, sum by (tool_name, le) (rate(agentic_harness_agent_tool_duration_seconds_bucket[5m])))');

  const isLoading = calls.isLoading;

  if (isLoading) {
    return (
      <div className="space-y-2">
        {[1, 2, 3].map((i) => <LoadingSkeleton key={i} className="h-12" />)}
      </div>
    );
  }

  const callSeries = calls.data?.series ?? [];
  const errSeries = errors.data?.series ?? [];
  const avgSeries = avgLat.data?.series ?? [];
  const p95Series = p95Lat.data?.series ?? [];

  if (callSeries.length === 0) {
    return (
      <EmptyState
        title="No tool data"
        description="No tool invocations recorded yet. Use an agent with tools to generate metrics."
      />
    );
  }

  const toolNames = [...new Set(callSeries.map((s) => s.labels['tool_name']))].filter(Boolean).sort();

  const findByTool = (list: MetricSeries[], name: string) =>
    list.find((s) => s.labels['tool_name'] === name);

  return (
    <div className="space-y-0">
      {/* Header row */}
      <div className="flex items-center gap-4 px-4 py-2 text-[10px] uppercase tracking-wider text-otel-text-mute border-b border-border">
        <div className="flex-[2]">Tool Name</div>
        <div className="flex-1 text-right">Calls</div>
        <div className="flex-1 text-right">Errors</div>
        <div className="flex-1 text-right">Avg Latency</div>
        <div className="flex-1 text-right">p95 Latency</div>
        <div className="w-24 text-right">Usefulness</div>
      </div>

      {toolNames.map((name) => {
        const c = findByTool(callSeries, name);
        const e = findByTool(errSeries, name);
        const avg = findByTool(avgSeries, name);
        const p95 = findByTool(p95Series, name);
        const callCount = c ? latestValue(c) : 0;
        const errCount = e ? latestValue(e) : 0;
        const errRate = callCount > 0 ? errCount / callCount : 0;
        // Usefulness heuristic: inverse of error rate, scaled 0-100
        const usefulness = callCount > 0 ? Math.max(0, (1 - errRate) * 100) : 0;

        return (
          <div
            key={name}
            className="flex items-center gap-4 px-4 py-3 border-b border-border/50 hover:bg-muted/30 transition-colors"
          >
            <div className="flex-[2] text-sm font-mono text-card-foreground truncate" title={name}>
              {name}
            </div>
            <div className="flex-1 text-right text-sm font-mono tabular-nums text-card-foreground">
              {fmt(callCount)}
            </div>
            <div className="flex-1 text-right text-sm font-mono tabular-nums">
              {errCount > 0 ? (
                <span className="text-otel-negative">{fmt(errCount)}</span>
              ) : (
                <span className="text-otel-text-mute">0</span>
              )}
            </div>
            <div className="flex-1 text-right text-sm font-mono tabular-nums text-card-foreground">
              {avg ? fmtMs(latestValue(avg) * 1000) : '--'}
            </div>
            <div className="flex-1 text-right text-sm font-mono tabular-nums text-card-foreground">
              {p95 ? fmtMs(latestValue(p95) * 1000) : '--'}
            </div>
            <div className="w-24 text-right">
              <Pill variant={usefulness >= 90 ? 'positive' : usefulness >= 70 ? 'warning' : 'negative'}>
                {usefulness.toFixed(0)}%
              </Pill>
            </div>
          </div>
        );
      })}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Page
// ---------------------------------------------------------------------------

export default function CatalogPage() {
  const [active, setActive] = useState('Models');

  return (
    <div className="space-y-4">
      <PageHeader title="Catalog" subtitle="Registered agents, models, and tools" />
      <TabNav items={tabs} active={active} onChange={setActive} />

      {active === 'Models' && <ModelsTab />}
      {active === 'Agents' && <AgentsTab />}
      {active === 'Tools' && <ToolsTab />}
    </div>
  );
}
