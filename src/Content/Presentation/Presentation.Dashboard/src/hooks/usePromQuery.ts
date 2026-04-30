import { useQuery } from '@tanstack/react-query';
import { queryRange } from '@/api/metrics';
import { useTimeRangeStore } from '@/stores/timeRangeStore';
import type { MetricsQueryResponse } from '@/api/types';

export function usePromQuery(promql: string, enabled = true) {
  const getRange = useTimeRangeStore((s) => s.getRange);
  const refreshIntervalSeconds = useTimeRangeStore((s) => s.refreshIntervalSeconds);
  const preset = useTimeRangeStore((s) => s.preset);
  const customStart = useTimeRangeStore((s) => s.customStart);
  const customEnd = useTimeRangeStore((s) => s.customEnd);

  return useQuery<MetricsQueryResponse>({
    queryKey: ['promRange', promql, preset, customStart, customEnd],
    queryFn: () => {
      const { start, end, step } = getRange();
      return queryRange(promql, start, end, step);
    },
    enabled,
    retry: false,
    refetchInterval: refreshIntervalSeconds * 1000,
    staleTime: (refreshIntervalSeconds * 1000) / 2,
  });
}
