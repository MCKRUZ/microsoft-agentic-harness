import { z } from 'zod';
import { useQuery } from '@tanstack/react-query';
import { apiClient } from '@/lib/apiClient';

const DeploymentsResponseSchema = z.object({
  deployments: z.array(z.string()),
  defaultDeployment: z.string(),
});

export type DeploymentsResponse = z.infer<typeof DeploymentsResponseSchema>;

export function useDeploymentsQuery() {
  return useQuery<DeploymentsResponse>({
    queryKey: ['config', 'deployments'],
    queryFn: () =>
      apiClient.get('/api/config/deployments').then((r) => DeploymentsResponseSchema.parse(r.data)),
    staleTime: 5 * 60_000,
  });
}
