import axios from 'axios';
import { apiClient } from './client';
import type { MetricsQueryResponse, MetricCatalogEntry, PrometheusHealthResponse } from './types';

const emptyMetrics: MetricsQueryResponse = {
  success: true,
  resultType: 'matrix',
  series: [],
};

function isNetworkOrProxyError(error: unknown): boolean {
  if (!axios.isAxiosError(error)) return false;
  if (!error.response) return true;
  const status = error.response.status;
  return status === 502 || status === 503 || status === 504;
}

export async function queryInstant(query: string, time?: string): Promise<MetricsQueryResponse> {
  try {
    const params: Record<string, string> = { query };
    if (time) params['time'] = time;
    const { data } = await apiClient.get<MetricsQueryResponse>('/api/metrics/instant', { params });
    return data;
  } catch (error) {
    if (isNetworkOrProxyError(error)) return emptyMetrics;
    throw error;
  }
}

export async function queryRange(
  query: string,
  start: string,
  end: string,
  step: string,
): Promise<MetricsQueryResponse> {
  try {
    const { data } = await apiClient.get<MetricsQueryResponse>('/api/metrics/range', {
      params: { query, start, end, step },
    });
    return data;
  } catch (error) {
    if (isNetworkOrProxyError(error)) return emptyMetrics;
    throw error;
  }
}

export async function getCatalog(): Promise<MetricCatalogEntry[]> {
  try {
    const { data } = await apiClient.get<MetricCatalogEntry[]>('/api/metrics/catalog');
    return data;
  } catch (error) {
    if (isNetworkOrProxyError(error)) return [];
    throw error;
  }
}

export async function getHealth(): Promise<PrometheusHealthResponse> {
  try {
    const { data } = await apiClient.get<PrometheusHealthResponse>('/api/metrics/health');
    return data;
  } catch (error) {
    if (isNetworkOrProxyError(error)) return { healthy: false, error: 'Backend unreachable' };
    throw error;
  }
}
