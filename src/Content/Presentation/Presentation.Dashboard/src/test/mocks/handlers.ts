import { http, HttpResponse } from 'msw';
import type { MetricsQueryResponse, MetricCatalogEntry, PrometheusHealthResponse } from '@/api/types';

function makeSeries(value: number, points = 10): MetricsQueryResponse {
  const now = Math.floor(Date.now() / 1000);
  return {
    success: true,
    resultType: 'matrix',
    series: [
      {
        labels: { __name__: 'test_metric' },
        dataPoints: Array.from({ length: points }, (_, i) => ({
          timestamp: now - (points - i) * 60,
          value: String(value + Math.random() * value * 0.1),
        })),
      },
    ],
  };
}

const catalogEntries: MetricCatalogEntry[] = [
  { id: 'test_metric', title: 'Test Metric', description: 'A test metric', query: 'test_query', chartType: 'stat', unit: 'count', category: 'overview', refreshIntervalSeconds: 15 },
];

export const handlers = [
  http.get('/api/metrics/range', () => {
    return HttpResponse.json(makeSeries(42));
  }),

  http.get('/api/metrics/instant', () => {
    return HttpResponse.json(makeSeries(42, 1));
  }),

  http.get('/api/metrics/catalog', () => {
    return HttpResponse.json(catalogEntries);
  }),

  http.get('/api/metrics/health', () => {
    return HttpResponse.json({ healthy: true, version: '2.51.0' } satisfies PrometheusHealthResponse);
  }),
];
