import { useNavigate } from 'react-router-dom';
import { useQueryClient } from '@tanstack/react-query';
import { useAppStore } from '@/stores/appStore';
import { useChatStore } from '@/stores/chatStore';
import { useAgentStream } from '@/hooks/useAgentStream';
import { useAgentHub } from '@/hooks/useAgentHub';
import { CONVERSATIONS_QUERY_KEY } from '@/features/conversations/useConversationsQuery';

/**
 * Returns a function that sends `text` as the user's next message in the active conversation: it adds
 * the optimistic user message, starts the streaming indicator, and dispatches the agent run — the same
 * three-step sequence the chat input and suggestion chips use. Shared by
 * {@link import('@/features/chat/ChatPanel').ChatPanel} and the inline form widget so this send path
 * lives in exactly one place.
 *
 * Server-side conversation creation is deferred to the first message: the id is minted here (never on
 * mount) when the composer is blank, and the conversation is registered lazily via
 * {@link import('@/hooks/useAgentHub').useAgentHub}'s `startConversation` only on that first send. This
 * is what keeps empty "phantom" conversations out of the sidebar — navigating to chat, switching agents,
 * or refreshing never creates a conversation; only actually sending a message does.
 *
 * Returns `false` (nothing sent) when no agent is selected, since a new conversation has nothing to bind
 * to. Once created, the conversation id is written to the store and reflected in the URL (`/chat/:id`) so
 * refresh and the back button restore it.
 */
export function useSendUserMessage(): (text: string) => boolean {
  const { sendMessage: agUiSend } = useAgentStream();
  const { startConversation, connectionState } = useAgentHub();
  const navigate = useNavigate();
  const queryClient = useQueryClient();

  return (text: string): boolean => {
    const { activeConversationId, selectedAgent, setActiveConversationId } = useAppStore.getState();
    // A new conversation must bind to an agent; without one there is nothing to send to.
    if (!selectedAgent) return false;

    // Both the first-message create and the run dispatch go over the SignalR hub, so a send before the
    // hub has connected (cold start / slow network) would reject. Refuse up front with a clear message
    // rather than minting an id and wedging on a failed StartConversation — the user can retry once the
    // status dot shows connected.
    if (connectionState !== 'connected') {
      useChatStore.getState().setError('Still connecting to the agent service — please try again in a moment.');
      return false;
    }

    // The active id is URL-driven. When the composer is blank (`/chat` with no id) we mint one here —
    // on send, never on mount — so an unused conversation is never registered server-side.
    const isNewConversation = activeConversationId === null;
    const conversationId = activeConversationId ?? crypto.randomUUID();

    // The first message is where the conversation must come to exist server-side. An empty transcript
    // means either a freshly minted id or an existing-but-unloaded one; `startConversation` is
    // idempotent (it returns the existing record when the id is already known), so registering it here
    // is safe either way, and the AG-UI run below requires the record to exist.
    const isFirstMessage = isNewConversation || useChatStore.getState().messages.length === 0;

    const userMessageId = crypto.randomUUID();
    useChatStore.getState().setConversationId(conversationId);
    useChatStore.getState().addMessage({
      id: userMessageId,
      role: 'user',
      content: text,
      timestamp: new Date(),
    });
    useChatStore.getState().startStreaming();

    if (isFirstMessage) {
      // Register the conversation, then dispatch the run — the run streams against a record that must
      // already exist. A failed registration surfaces as an error rather than a stuck "Conversation
      // not found" run.
      void (async () => {
        try {
          await startConversation(selectedAgent, conversationId);
          // The conversation now exists (and carries this first message after the run) — refresh the
          // sidebar so it appears. Deferring this to the first message is what keeps phantoms out.
          void queryClient.invalidateQueries({ queryKey: CONVERSATIONS_QUERY_KEY });
          agUiSend(conversationId, userMessageId, text);
        } catch (err: unknown) {
          // Roll the optimistic first message back out. Leaving it in place would make the transcript
          // non-empty, so `isFirstMessage` (messages.length === 0) would be false on every retry — the
          // next send would skip StartConversation and dispatch against a conversation that was never
          // created, wedging it until a full page reload. Clearing restores the empty transcript so a
          // retry re-attempts creation (the minted id in the store/URL is reused, and StartConversation
          // is idempotent).
          const chat = useChatStore.getState();
          chat.clearMessages();
          chat.setError(err instanceof Error ? err.message : 'Failed to start the conversation.');
        }
      })();
    } else {
      agUiSend(conversationId, userMessageId, text);
    }

    if (isNewConversation) {
      // Reflect the minted id in the store and URL so refresh/back restore this exact conversation.
      setActiveConversationId(conversationId);
      navigate(`/chat/${conversationId}`, { replace: true });
    }

    return true;
  };
}
