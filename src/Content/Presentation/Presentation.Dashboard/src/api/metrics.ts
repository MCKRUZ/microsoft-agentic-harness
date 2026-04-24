import { apiClient } from './client';
import type { MetricsQueryResponse, MetricCatalogEntry, PrometheusHealthResponse } from './types';

export async function queryInstant(query: string, time?: string): Promise<MetricsQueryResponse> {
  const params: Record<string, string> = { query };
  if (time) params['time'] = time;
  const { data } = await apiClient.get<MetricsQueryResponse>('/api/metrics/instant', { params });
  return data;
}

export async function queryRange(
  query: string,
  start: string,
  end: string,
  step: string,
): Promise<MetricsQueryResponse> {
  const { data } = await apiClient.get<MetricsQueryResponse>('/api/metrics/range', {
    params: { query, start, end, step },
  });
  return data;
}

export async function getCatalog(): Promise<MetricCatalogEntry[]> {
  const { data } = await apiClient.get<MetricCatalogEntry[]>('/api/metrics/catalog');
  return data;
}

export async function getHealth(): Promise<PrometheusHealthResponse> {
  const { data } = await apiClient.get<PrometheusHealthResponse>('/api/metrics/health');
  return data;
}
