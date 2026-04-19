import { z } from 'zod';
import { useQuery } from '@tanstack/react-query';
import { apiClient } from '@/lib/apiClient';

const AgentSchema = z.object({
  id: z.string(),
  name: z.string(),
  description: z.string().optional(),
});

export type Agent = z.infer<typeof AgentSchema>;

export function useAgentsQuery() {
  return useQuery<Agent[]>({
    queryKey: ['agents'],
    queryFn: () =>
      apiClient.get('/api/agents').then((r) => z.array(AgentSchema).parse(r.data)),
    staleTime: 60_000,
  });
}
