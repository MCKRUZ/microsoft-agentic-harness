import { useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '@/lib/apiClient';
import { CONVERSATIONS_QUERY_KEY } from './useConversationsQuery';

export function useDeleteConversation() {
  const queryClient = useQueryClient();
  return useMutation<void, Error, string>({
    mutationFn: async (id: string) => {
      await apiClient.delete(`/api/conversations/${id}`);
    },
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: CONVERSATIONS_QUERY_KEY });
    },
  });
}
