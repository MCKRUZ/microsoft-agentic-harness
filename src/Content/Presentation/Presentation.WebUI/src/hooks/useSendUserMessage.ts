import { useAppStore } from '@/stores/appStore';
import { useChatStore } from '@/stores/chatStore';
import { useAgentStream } from '@/hooks/useAgentStream';

/**
 * Returns a function that sends `text` as the user's next message in the active conversation: it adds
 * the optimistic user message, starts the streaming indicator, and dispatches the agent run — the same
 * three-step sequence the chat input and suggestion chips use. Returns `false` when there is no active
 * conversation (nothing sent). Shared by {@link import('@/features/chat/ChatPanel').ChatPanel} and the
 * inline form widget so this send path lives in exactly one place.
 */
export function useSendUserMessage(): (text: string) => boolean {
  const { sendMessage: agUiSend } = useAgentStream();

  return (text: string): boolean => {
    const conversationId = useAppStore.getState().activeConversationId;
    if (!conversationId) return false;

    const userMessageId = crypto.randomUUID();
    useChatStore.getState().addMessage({
      id: userMessageId,
      role: 'user',
      content: text,
      timestamp: new Date(),
    });
    useChatStore.getState().startStreaming();
    agUiSend(conversationId, userMessageId, text);
    return true;
  };
}
