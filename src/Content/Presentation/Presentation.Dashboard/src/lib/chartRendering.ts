import { getCatalog, queryRange } from '@/api/metrics';
import { queryClient } from '@/app/queryClient';
import { asString } from '@/lib/utils';
import { useTimeRangeStore } from '@/stores/timeRangeStore';
import type { MetricCatalogEntry } from '@/api/types';
import type { ChartSpec } from '@/stores/chatStore';

/** Parameters the agent supplies to `render_chart`. */
export interface RenderChartParams {
  metricId?: string;
  promQL?: string;
  chartType?: string;
  title?: string;
}

/** Normalizes a catalog/agent chart type to one the panel can render. */
function normalizeChartType(raw: string | null | undefined): 'timeseries' | 'bar' | 'pie' {
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

/**
 * Loads the dashboard metric catalog, cached for the session. Uses the same server catalog the agent's
 * `list_metrics` tool exposes (so a metricId the agent picked always resolves), routed through the
 * shared query cache so repeated chart requests don't re-fetch it. The catalog is static at runtime.
 */
function loadCatalog(): Promise<MetricCatalogEntry[]> {
  return queryClient.fetchQuery({
    queryKey: ['metricsCatalog'],
    queryFn: getCatalog,
    staleTime: Infinity,
  });
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
  let chartType = asString(params.chartType);

  if (metricId) {
    const entry = (await loadCatalog()).find((e) => e.id === metricId);
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
