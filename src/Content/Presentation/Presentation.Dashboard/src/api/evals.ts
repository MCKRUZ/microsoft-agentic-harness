import { apiClient } from './client';

/**
 * Mirrors {@link Application.AI.Common.Evaluation.Models.EvalRunSummary} on the server.
 * Field names and shape are part of the API contract — server-side controller
 * tests pin the JSON shape; renaming a property here will break the dashboard.
 */
export interface EvalRunSummary {
  runId: string;
  startedAtUtc: string;
  completedAtUtc: string;
  duration: string; // .NET TimeSpan as serialised by ASP.NET (HH:MM:SS.ffffff or ISO8601-duration).
  passedCount: number;
  failedCount: number;
  warnedCount: number;
  erroredCount: number;
  totalCostUsd: number;
  repeats: number;
  overallVerdict: 'Pass' | 'Fail' | 'Warn';
  receivedAtUtc: string;
  passRate: number;
}

export interface EvalCase {
  id: string;
  input: string;
  expectedOutput?: string | null;
  retrievedContext?: string | null;
  tags: string[];
}

export interface MetricScore {
  metricKey: string;
  score: number;
  verdict: 'Pass' | 'Fail' | 'Warn';
  reasoning?: string | null;
  costUsd: number;
  /** Panel ("jury") agreement when several judges scored this metric; null for single-judge/non-LLM metrics. */
  consensus?: 'Consensus' | 'Split' | 'Conflict' | null;
  /** Spread (max − min) of the panelists' scores; null when {@link consensus} is null. */
  spread?: number | null;
}

export interface EvalResult {
  case: EvalCase;
  outputPerRepeat: string[];
  aggregatedScores: Record<string, MetricScore>;
  verdict: 'Pass' | 'Fail' | 'Warn';
  costUsd: number;
  error?: string | null;
}

export interface EvalDataset {
  name: string;
  version: string;
  description?: string | null;
  cases: EvalCase[];
}

export interface EvalRunReport {
  runId: string;
  startedAtUtc: string;
  completedAtUtc: string;
  duration: string;
  datasets: EvalDataset[];
  results: EvalResult[];
  passedCount: number;
  failedCount: number;
  warnedCount: number;
  erroredCount: number;
  totalCostUsd: number;
  repeats: number;
  overallVerdict: 'Pass' | 'Fail' | 'Warn';
  warnings: string[];
}

export interface PromptVersion {
  major: number;
  minor: number;
}

export interface PromptVersionComparisonRow {
  version: PromptVersion;
  metricKey: string;
  averageScore: number;
  sampleSize: number;
}

export interface RegressedCaseRow {
  caseId: string;
  datasetName: string;
  metricKey: string;
  baselineScore: number;
  currentScore: number;
  delta: number;
}

export interface IngestEvalRunResult {
  runId: string;
  inserted: boolean;
  receivedAtUtc: string;
}

export async function fetchEvalRuns(take = 50): Promise<EvalRunSummary[]> {
  const { data } = await apiClient.get<EvalRunSummary[]>('/api/evals/runs', { params: { take } });
  return data;
}

export async function fetchEvalRunDetail(runId: string): Promise<EvalRunReport> {
  const { data } = await apiClient.get<EvalRunReport>(`/api/evals/runs/${encodeURIComponent(runId)}`);
  return data;
}

export async function fetchPromptVersionComparison(
  promptName: string,
): Promise<PromptVersionComparisonRow[]> {
  const { data } = await apiClient.get<PromptVersionComparisonRow[]>(
    `/api/evals/prompts/${encodeURIComponent(promptName)}/compare`,
  );
  return data;
}

export async function fetchRegressedCases(
  current: string,
  baseline: string,
  take = 20,
): Promise<RegressedCaseRow[]> {
  const { data } = await apiClient.get<RegressedCaseRow[]>('/api/evals/regressions', {
    params: { current, baseline, take },
  });
  return data;
}
