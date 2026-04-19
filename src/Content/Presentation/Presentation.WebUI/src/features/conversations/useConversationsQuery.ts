import { z } from 'zod';
import { useQuery } from '@tanstack/react-query';
import { apiClient } from '@/lib/apiClient';

const ConversationSummarySchema = z.object({
  id: z.string(),
  agentName: z.string(),
  userId: z.string(),
  createdAt: z.string(),
  updatedAt: z.string(),
  title: z.string().nullable().optional(),
  messages: z.array(z.unknown()).optional(),
});

export type ConversationSummary = z.infer<typeof ConversationSummarySchema> & {
  messageCount: number;
};

export const CONVERSATIONS_QUERY_KEY = ['conversations'] as const;

export function useConversationsQuery() {
  return useQuery<ConversationSummary[]>({
    queryKey: CONVERSATIONS_QUERY_KEY,
    queryFn: async () => {
      const r = await apiClient.get('/api/conversations');
      const parsed = z.array(ConversationSummarySchema).parse(r.data);
      return parsed
        .map((c) => ({ ...c, messageCount: c.messages?.length ?? 0 }))
        .sort((a, b) => b.updatedAt.localeCompare(a.updatedAt));
    },
    staleTime: 10_000,
  });
}
