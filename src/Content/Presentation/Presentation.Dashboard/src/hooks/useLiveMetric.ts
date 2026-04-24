import { useMemo } from 'react';
import { usePromQuery } from './usePromQuery';
import { useTelemetryStore } from '@/stores/telemetryStore';
import type { MetricSeries } from '@/api/types';

export function useLiveMetric(metricName: string, promql: string) {
  const historical = usePromQuery(promql);
  const events = useTelemetryStore((s) => s.events);

  const series = useMemo<MetricSeries[]>(() => {
    const base = historical.data?.series ?? [];
    if (base.length === 0) return base;

    const relevantEvents = events.filter(
      (e) => e.type === 'MetricsUpdate' && e.data['metric'] === metricName,
    );

    if (relevantEvents.length === 0) return base;

    const lastHistoricalTs = base[0]?.dataPoints[base[0].dataPoints.length - 1]?.timestamp ?? 0;
    const livePoints = relevantEvents
      .filter((e) => e.timestamp / 1000 > lastHistoricalTs)
      .map((e) => ({
        timestamp: Math.floor(e.timestamp / 1000),
        value: String(e.data['value'] ?? '0'),
      }));

    if (livePoints.length === 0) return base;

    return base.map((s, i) =>
      i === 0
        ? { ...s, dataPoints: [...s.dataPoints, ...livePoints] }
        : s,
    );
  }, [historical.data, events, metricName]);

  return {
    series,
    isLoading: historical.isLoading,
    isError: historical.isError,
    error: historical.error,
  };
}
