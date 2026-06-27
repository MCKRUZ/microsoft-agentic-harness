import { z } from 'zod';
import { useQuery } from '@tanstack/react-query';
import { apiClient } from '@/api/client';

const AiProviderStatusSchema = z.object({
  configured: z.boolean(),
  clientType: z.string(),
  defaultDeployment: z.string(),
  missingSettings: z.array(z.string()),
});

export type AiProviderStatus = z.infer<typeof AiProviderStatusSchema>;

/**
 * Polls `/api/config/status` so the UI can warn when the active AI provider is not configured
 * (missing endpoint/key), instead of letting agent turns fail with an opaque error.
 */
export function useAiProviderStatus() {
  return useQuery<AiProviderStatus>({
    queryKey: ['ai-provider-status'],
    queryFn: () =>
      apiClient.get('/api/config/status').then((r) => AiProviderStatusSchema.parse(r.data)),
    staleTime: 30_000,
    // Recover on its own: a transient failure on first load (AgentHub still starting) must not hide
    // the banner for the whole session, and the banner should clear once the provider is fixed.
    retry: 3,
    refetchInterval: 60_000,
  });
}
