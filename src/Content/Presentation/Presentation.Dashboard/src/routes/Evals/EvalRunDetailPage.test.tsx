import { screen } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import { renderRoutedPage } from '@/test/helpers/renderPage';

// A report with three case rows: a Consensus jury metric, a Conflict jury metric, and a
// single-judge / non-panel metric (no consensus) that must render an em-dash.
const { mockReport } = vi.hoisted(() => {
  const score = (consensus: string | null, spread: number | null) => ({
    metricKey: 'llm_judge',
    score: 0.85,
    verdict: 'Pass',
    costUsd: 0,
    consensus,
    spread,
  });
  return {
    mockReport: {
      runId: 'run-1',
      startedAtUtc: new Date(0).toISOString(),
      completedAtUtc: new Date(1000).toISOString(),
      duration: '00:00:01',
      datasets: [],
      results: [
        {
          case: { id: 'agree', input: '', tags: [] },
          outputPerRepeat: [],
          aggregatedScores: { llm_judge: score('Consensus', 0.1) },
          verdict: 'Pass',
          costUsd: 0,
          error: null,
        },
        {
          case: { id: 'disagree', input: '', tags: [] },
          outputPerRepeat: [],
          aggregatedScores: { llm_judge: score('Conflict', 0.8) },
          verdict: 'Pass',
          costUsd: 0,
          error: null,
        },
        {
          case: { id: 'single', input: '', tags: [] },
          outputPerRepeat: [],
          aggregatedScores: { exact_match: { metricKey: 'exact_match', score: 1, verdict: 'Pass', costUsd: 0 } },
          verdict: 'Pass',
          costUsd: 0,
          error: null,
        },
      ],
      passedCount: 3,
      failedCount: 0,
      warnedCount: 0,
      erroredCount: 0,
      totalCostUsd: 0,
      repeats: 1,
      overallVerdict: 'Pass',
      warnings: [],
    },
  };
});

vi.mock('@/hooks/useEvalRunDetail', () => ({
  useEvalRunDetail: () => ({ data: mockReport, isLoading: false, isError: false, error: null }),
}));

import EvalRunDetailPage from './EvalRunDetailPage';

function renderPage() {
  return renderRoutedPage(EvalRunDetailPage, { route: '/evals/run-1', path: '/evals/:runId' });
}

describe('EvalRunDetailPage consensus column', () => {
  it('renders a dedicated Consensus column header', () => {
    renderPage();
    expect(screen.getByRole('columnheader', { name: 'Consensus' })).toBeInTheDocument();
  });

  it('shows the bucket and spread, color-coded green for agreement', () => {
    renderPage();
    const cell = screen.getByText(/Consensus \(Δ0\.10\)/);
    expect(cell.className).toContain('text-otel-positive');
  });

  it('shows conflict color-coded red with its spread', () => {
    renderPage();
    const cell = screen.getByText(/Conflict \(Δ0\.80\)/);
    expect(cell.className).toContain('text-otel-negative');
  });

  it('renders an em-dash for a metric scored by a single judge (no panel)', () => {
    renderPage();
    // The 'single' row's only metric has no consensus; no bucket chip should appear for it.
    expect(screen.queryByText(/Split/)).not.toBeInTheDocument();
    // Both a Consensus and a Conflict chip exist; the third row contributes none.
    expect(screen.getByText(/Consensus \(Δ0\.10\)/)).toBeInTheDocument();
    expect(screen.getByText(/Conflict \(Δ0\.80\)/)).toBeInTheDocument();
  });
});
