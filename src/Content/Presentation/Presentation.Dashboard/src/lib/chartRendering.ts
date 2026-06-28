import { getCatalog, queryRange } from '@/api/metrics';
import { useTimeRangeStore } from '@/stores/timeRangeStore';
import type { ChartSpec } from '@/stores/chatStore';

/** Parameters the agent supplies to `render_chart`. */
export interface RenderChartParams {
  metricId?: string;
  promQL?: string;
  chartType?: string;
  title?: string;
}

/** Normalizes a catalog/agent chart type to one the panel can render. */
function normalizeChartType(raw: string | undefined): 'timeseries' | 'bar' | 'pie' {
  switch ((raw ?? '').toLowerCase()) {
    case 'bar':
      return 'bar';
    case 'pie':
      return 'pie';
    default:
      // stat / gauge / line / area / timeseries all render as a time series.
      return 'timeseries';
  }
}

function asString(value: unknown): string | null {
  return typeof value === 'string' && value.trim().length > 0 ? value.trim() : null;
}

/**
 * Resolves a `render_chart` request to a concrete {@link ChartSpec} plus a short textual summary the
 * agent can narrate. A `metricId` is looked up in the dashboard's metric catalog (single source of
 * truth); otherwise a raw `promQL` query is used. Data is fetched over the dashboard's current time
 * range via the existing metrics API, so the chart matches what the dashboard would show.
 */
export async function buildChart(
  params: RenderChartParams,
): Promise<{ chart: ChartSpec; summary: string }> {
  const metricId = asString(params.metricId);
  const promQL = asString(params.promQL);

  let query: string;
  let title: string;
  let unit: string | undefined;
  let chartType: string | undefined = asString(params.chartType) ?? undefined;

  if (metricId) {
    const entry = (await getCatalog()).find((e) => e.id === metricId);
    if (!entry) {
      throw new Error(`Unknown metric "${metricId}".`);
    }
    query = entry.query;
    title = asString(params.title) ?? entry.title;
    unit = entry.unit;
    chartType = chartType ?? entry.chartType;
  } else if (promQL) {
    query = promQL;
    title = asString(params.title) ?? 'Chart';
  } else {
    throw new Error('Provide a metricId or a promQL query.');
  }

  const { start, end, step } = useTimeRangeStore.getState().getRange();
  const response = await queryRange(query, start, end, step);
  const series = response.series ?? [];
  const renderType = normalizeChartType(chartType);

  const chart: ChartSpec = { title, chartType: renderType, unit, series };
  const summary =
    series.length === 0
      ? `No data available for "${title}" over the current time range.`
      : `Rendered a ${renderType} chart of "${title}" (${series.length} series).`;

  return { chart, summary };
}
