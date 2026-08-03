import { useNavigate } from 'react-router';
import { useEvalRuns } from '@/hooks/useEvalRuns';
import { useEvalRunLive } from '@/hooks/useEvalRunLive';
import { PageHeader } from '@/components/primitives/PageHeader';
import { LoadingSkeleton } from '@/components/panels/LoadingSkeleton';
import { EmptyState } from '@/components/panels/EmptyState';
import type { EvalRunSummary } from '@/api/evals';

function verdictBadgeClass(verdict: EvalRunSummary['overallVerdict']): string {
  switch (verdict) {
    case 'Pass':
      return 'bg-otel-positive/20 text-otel-positive border border-otel-positive/40';
    case 'Fail':
      return 'bg-otel-negative/20 text-otel-negative border border-otel-negative/40';
    case 'Warn':
      return 'bg-otel-warning/20 text-otel-warning border border-otel-warning/40';
  }
}

function formatPercent(value: number): string {
  return `${(value * 100).toFixed(1)}%`;
}

function formatCost(usd: number): string {
  if (usd >= 1) return `$${usd.toFixed(2)}`;
  if (usd >= 0.01) return `$${usd.toFixed(3)}`;
  return `$${usd.toFixed(4)}`;
}

function formatTimestamp(iso: string): string {
  return new Date(iso).toLocaleString();
}

export default function EvalsListPage() {
  const navigate = useNavigate();
  const { data: runs, isLoading, isError, error } = useEvalRuns(50);

  // SignalR live invalidation — runs alongside polling so a dropped connection
  // doesn't strand the UI. No effect on render shape, just on refresh latency.
  useEvalRunLive();

  if (isLoading) {
    return (
      <div className="space-y-6">
        <PageHeader title="Evals" subtitle="Recent eval runs" />
        <LoadingSkeleton />
      </div>
    );
  }

  if (isError) {
    return (
      <div className="space-y-6">
        <PageHeader title="Evals" subtitle="Recent eval runs" />
        <div className="text-otel-negative">Failed to load runs: {String(error)}</div>
      </div>
    );
  }

  if (!runs || runs.length === 0) {
    return (
      <div className="space-y-6">
        <PageHeader title="Evals" subtitle="Recent eval runs" />
        <EmptyState
          title="No runs ingested yet"
          description="Run the EvalRunner CLI with --ingest-url pointed at this dashboard to populate run history."
        />
      </div>
    );
  }

  return (
    <div className="space-y-6">
      <PageHeader title="Evals" subtitle={`${runs.length} recent runs`} />

      <div className="overflow-x-auto rounded-lg border border-border">
        <table className="w-full text-sm">
          <thead className="bg-muted/30 text-muted-foreground">
            <tr>
              <th className="px-3 py-2 text-left">Run ID</th>
              <th className="px-3 py-2 text-left">Started</th>
              <th className="px-3 py-2 text-right">Pass</th>
              <th className="px-3 py-2 text-right">Fail</th>
              <th className="px-3 py-2 text-right">Warn</th>
              <th className="px-3 py-2 text-right">Pass Rate</th>
              <th className="px-3 py-2 text-right">Cost</th>
              <th className="px-3 py-2 text-right">Repeats</th>
              <th className="px-3 py-2 text-left">Verdict</th>
            </tr>
          </thead>
          <tbody>
            {runs.map((run) => (
              <tr
                key={run.runId}
                onClick={() => navigate(`/evals/${encodeURIComponent(run.runId)}`)}
                className="cursor-pointer border-t border-border hover:bg-muted/30"
              >
                <td className="px-3 py-2 font-mono text-xs">{run.runId}</td>
                <td className="px-3 py-2 text-muted-foreground">{formatTimestamp(run.startedAtUtc)}</td>
                <td className="px-3 py-2 text-right font-mono tabular-nums">{run.passedCount}</td>
                <td className="px-3 py-2 text-right font-mono tabular-nums">{run.failedCount}</td>
                <td className="px-3 py-2 text-right font-mono tabular-nums">{run.warnedCount}</td>
                <td className="px-3 py-2 text-right font-mono tabular-nums">{formatPercent(run.passRate)}</td>
                <td className="px-3 py-2 text-right font-mono tabular-nums">{formatCost(run.totalCostUsd)}</td>
                <td className="px-3 py-2 text-right font-mono tabular-nums">{run.repeats}</td>
                <td className="px-3 py-2">
                  <span className={`inline-block rounded px-2 py-0.5 text-xs ${verdictBadgeClass(run.overallVerdict)}`}>
                    {run.overallVerdict}
                  </span>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
