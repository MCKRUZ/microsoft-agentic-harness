import { useQuery } from '@tanstack/react-query';
import { apiClient } from '@/api/client';

export type SloVerdict = 'Met' | 'AtRisk' | 'Breached';

export interface SloStatus {
  id: string;
  name: string;
  description: string;
  target: number;
  currentValue: number;
  unit: string;
  comparator: string;
  status: SloVerdict;
  errorBudgetRemainingPercent: number;
  sparklineQuery: string;
}

async function fetchSloStatus(): Promise<SloStatus[]> {
  try {
    const { data } = await apiClient.get<SloStatus[]>('/api/metrics/slo');
    return data;
  } catch {
    return [];
  }
}

export function useSloStatus() {
  return useQuery<SloStatus[]>({
    queryKey: ['slo-status'],
    queryFn: fetchSloStatus,
    refetchInterval: 30_000,
    staleTime: 15_000,
  });
}
