import axios from 'axios';
import { apiClient } from './client';
import type { MetricsQueryResponse, MetricCatalogEntry, PrometheusHealthResponse } from './types';

const emptyMetrics: MetricsQueryResponse = {
  success: true,
  resultType: 'matrix',
  series: [],
};

function lastTimestamp(s: { dataPoints: { timestamp: number }[] }): number {
  return s.dataPoints.length > 0 ? s.dataPoints[s.dataPoints.length - 1]!.timestamp : 0;
}

function stripVectorFallback(response: MetricsQueryResponse): MetricsQueryResponse {
  if (response.series.length <= 1) return response;
  const withLabels = response.series.filter(s => Object.keys(s.labels).length > 0);
  if (withLabels.length === 0) return response;
  const fallback = response.series.find(s => Object.keys(s.labels).length === 0);
  if (!fallback) return { ...response, series: withLabels };
  const fallbackTs = lastTimestamp(fallback);
  const freshLabeled = withLabels.filter(s => lastTimestamp(s) >= fallbackTs);
  if (freshLabeled.length > 0) return { ...response, series: freshLabeled };
  return { ...response, series: [fallback] };
}

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
    return stripVectorFallback(data);
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
    return stripVectorFallback(data);
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
