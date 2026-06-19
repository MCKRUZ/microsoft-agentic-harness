import { useParams, Link } from 'react-router-dom';
import { useEvalRunDetail } from '@/hooks/useEvalRunDetail';
import { PageHeader } from '@/components/primitives/PageHeader';
import { LoadingSkeleton } from '@/components/panels/LoadingSkeleton';
import type { EvalResult, MetricScore } from '@/api/evals';

function verdictColor(verdict: string): string {
  switch (verdict) {
    case 'Pass': return 'text-otel-positive';
    case 'Fail': return 'text-otel-negative';
    case 'Warn': return 'text-otel-warning';
    default: return 'text-muted-foreground';
  }
}

function formatScore(s: MetricScore): string {
  return `${s.score.toFixed(3)} (${s.verdict})`;
}

// Jury agreement palette: green = judges agreed, amber = split, red = conflict (look here).
function consensusColor(bucket: string): string {
  switch (bucket) {
    case 'Consensus': return 'text-otel-positive';
    case 'Split': return 'text-otel-warning';
    case 'Conflict': return 'text-otel-negative';
    default: return 'text-muted-foreground';
  }
}

function ConsensusCell({ scores }: { scores: Record<string, MetricScore> }) {
  const withConsensus = Object.entries(scores).filter(([, v]) => v.consensus != null);
  if (withConsensus.length === 0) {
    return <span className="text-muted-foreground">—</span>;
  }
  return (
    <div className="space-y-0.5 text-xs">
      {withConsensus.map(([k, v]) => (
        <div key={k}>
          <span className="text-muted-foreground">{k}:</span>{' '}
          <span className={consensusColor(v.consensus!)}>
            {v.consensus}
            {v.spread != null ? ` (Δ${v.spread.toFixed(2)})` : ''}
          </span>
        </div>
      ))}
    </div>
  );
}

function CaseRow({ result }: { result: EvalResult }) {
  return (
    <tr className="border-t border-border">
      <td className="px-3 py-2 font-mono text-xs">{result.case.id}</td>
      <td className={`px-3 py-2 font-medium ${verdictColor(result.verdict)}`}>{result.verdict}</td>
      <td className="px-3 py-2">
        <div className="space-y-0.5 text-xs">
          {Object.entries(result.aggregatedScores).map(([k, v]) => (
            <div key={k}>
              <span className="text-muted-foreground">{k}:</span> {formatScore(v)}
            </div>
          ))}
        </div>
      </td>
      <td className="px-3 py-2"><ConsensusCell scores={result.aggregatedScores} /></td>
      <td className="px-3 py-2 text-right font-mono tabular-nums">${result.costUsd.toFixed(4)}</td>
      <td className="px-3 py-2 text-xs text-muted-foreground">{result.error ?? '—'}</td>
    </tr>
  );
}

export default function EvalRunDetailPage() {
  const { runId } = useParams<{ runId: string }>();
  const { data: report, isLoading, isError, error } = useEvalRunDetail(runId);

  if (isLoading) {
    return (
      <div className="space-y-6">
        <PageHeader title="Run Detail" subtitle={runId ?? ''} />
        <LoadingSkeleton />
      </div>
    );
  }

  if (isError || !report) {
    return (
      <div className="space-y-6">
        <PageHeader title="Run Detail" subtitle={runId ?? ''} />
        <div className="text-otel-negative">
          Failed to load run: {String(error ?? 'not found')}
        </div>
        <Link to="/evals" className="text-cat-accent underline">← Back to runs</Link>
      </div>
    );
  }

  return (
    <div className="space-y-6">
      <PageHeader
        title={`Run ${report.runId}`}
        subtitle={`Started ${new Date(report.startedAtUtc).toLocaleString()} · ${report.overallVerdict}`}
      />

      <div className="grid grid-cols-2 gap-4 sm:grid-cols-4">
        <div className="rounded border border-border p-3">
          <div className="text-xs text-muted-foreground">Pass / Fail / Warn</div>
          <div className="text-lg font-mono tabular-nums">
            <span className="text-otel-positive">{report.passedCount}</span>
            {' / '}
            <span className="text-otel-negative">{report.failedCount}</span>
            {' / '}
            <span className="text-otel-warning">{report.warnedCount}</span>
          </div>
        </div>
        <div className="rounded border border-border p-3">
          <div className="text-xs text-muted-foreground">Errored</div>
          <div className="text-lg font-mono tabular-nums">{report.erroredCount}</div>
        </div>
        <div className="rounded border border-border p-3">
          <div className="text-xs text-muted-foreground">Total Cost</div>
          <div className="text-lg font-mono tabular-nums">${report.totalCostUsd.toFixed(4)}</div>
        </div>
        <div className="rounded border border-border p-3">
          <div className="text-xs text-muted-foreground">Repeats</div>
          <div className="text-lg font-mono tabular-nums">{report.repeats}</div>
        </div>
      </div>

      {report.warnings.length > 0 && (
        <div className="rounded border border-otel-warning/40 bg-otel-warning/10 p-3">
          <div className="mb-2 text-sm font-semibold text-otel-warning">Warnings</div>
          <ul className="list-inside list-disc text-xs">
            {report.warnings.map((w, i) => <li key={i}>{w}</li>)}
          </ul>
        </div>
      )}

      <div className="overflow-x-auto rounded-lg border border-border">
        <table className="w-full text-sm">
          <thead className="bg-muted/30 text-muted-foreground">
            <tr>
              <th className="px-3 py-2 text-left">Case ID</th>
              <th className="px-3 py-2 text-left">Verdict</th>
              <th className="px-3 py-2 text-left">Scores</th>
              <th className="px-3 py-2 text-left">Consensus</th>
              <th className="px-3 py-2 text-right">Cost</th>
              <th className="px-3 py-2 text-left">Error</th>
            </tr>
          </thead>
          <tbody>
            {report.results.map((r) => <CaseRow key={r.case.id} result={r} />)}
          </tbody>
        </table>
      </div>

      <Link to="/evals" className="text-cat-accent underline">← Back to runs</Link>
    </div>
  );
}
