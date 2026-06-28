import { describe, it, expect, beforeEach, vi } from 'vitest';

const { getCatalog, queryRange } = vi.hoisted(() => ({
  getCatalog: vi.fn(),
  queryRange: vi.fn(),
}));

vi.mock('@/api/metrics', () => ({ getCatalog, queryRange }));

// Bypass the real query cache so each test sees its own getCatalog mock (no cross-test caching).
vi.mock('@/app/queryClient', () => ({
  queryClient: { fetchQuery: ({ queryFn }: { queryFn: () => unknown }) => queryFn() },
}));

import { buildChart } from './chartRendering';

const CATALOG = [
  { id: 'tokens_by_model', title: 'Tokens by Model', description: '', query: 'sum by (model) (tokens)', chartType: 'pie', unit: 'tokens', category: 'tokens' },
];

const ONE_SERIES = {
  success: true,
  resultType: 'matrix',
  series: [{ labels: { model: 'gpt-4o' }, dataPoints: [{ timestamp: 1, value: '5' }] }],
};

describe('buildChart', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    getCatalog.mockResolvedValue(CATALOG);
    queryRange.mockResolvedValue(ONE_SERIES);
  });

  it('resolves a metricId via the catalog and queries its PromQL', async () => {
    const { chart, summary } = await buildChart({ metricId: 'tokens_by_model' });
    expect(queryRange).toHaveBeenCalledWith('sum by (model) (tokens)', expect.any(String), expect.any(String), expect.any(String));
    expect(chart.title).toBe('Tokens by Model');
    expect(chart.chartType).toBe('pie');
    expect(chart.unit).toBe('tokens');
    expect(chart.series).toHaveLength(1);
    expect(summary).toContain('pie');
  });

  it('lets the agent override the chart type', async () => {
    const { chart } = await buildChart({ metricId: 'tokens_by_model', chartType: 'bar' });
    expect(chart.chartType).toBe('bar');
  });

  it('uses a raw promQL query when no metricId is given', async () => {
    const { chart } = await buildChart({ promQL: 'up', title: 'Up' });
    expect(queryRange).toHaveBeenCalledWith('up', expect.any(String), expect.any(String), expect.any(String));
    expect(chart.title).toBe('Up');
    expect(chart.chartType).toBe('timeseries');
  });

  it('rejects an unknown metricId', async () => {
    await expect(buildChart({ metricId: 'does-not-exist' })).rejects.toThrow(/unknown metric/i);
  });

  it('rejects when neither metricId nor promQL is supplied', async () => {
    await expect(buildChart({})).rejects.toThrow();
  });

  it('summarizes an empty result as no data', async () => {
    queryRange.mockResolvedValue({ success: true, resultType: 'matrix', series: [] });
    const { chart, summary } = await buildChart({ promQL: 'up' });
    expect(chart.series).toHaveLength(0);
    expect(summary.toLowerCase()).toContain('no data');
  });
});
