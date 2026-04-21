import { useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '@/lib/apiClient';
import { CONVERSATIONS_QUERY_KEY, type ConversationSummary } from './useConversationsQuery';

export function useDeleteConversation() {
  const queryClient = useQueryClient();
  return useMutation<void, Error, string>({
    mutationFn: async (id: string) => {
      await apiClient.delete(`/api/conversations/${id}`);
    },
    onMutate: async (id: string) => {
      await queryClient.cancelQueries({ queryKey: CONVERSATIONS_QUERY_KEY });
      const previous = queryClient.getQueryData<ConversationSummary[]>(CONVERSATIONS_QUERY_KEY);
      queryClient.setQueryData<ConversationSummary[]>(
        CONVERSATIONS_QUERY_KEY,
        (old) => old?.filter((c) => c.id !== id) ?? [],
      );
      return { previous };
    },
    onError: (_err, _id, context) => {
      if (context?.previous) {
        queryClient.setQueryData(CONVERSATIONS_QUERY_KEY, context.previous);
      }
    },
    onSettled: () => {
      void queryClient.invalidateQueries({ queryKey: CONVERSATIONS_QUERY_KEY });
    },
  });
}
