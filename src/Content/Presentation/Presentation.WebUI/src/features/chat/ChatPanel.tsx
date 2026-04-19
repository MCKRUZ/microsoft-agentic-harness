import { useEffect, useState } from 'react';
import { useChatStore } from './useChatStore';
import { useAppStore } from '@/stores/appStore';
import { useHubActionsStore } from '@/stores/hubActionsStore';
import { useAgentHub } from '@/hooks/useAgentHub';
import { MessageList } from './MessageList';
import { TypingIndicator } from './TypingIndicator';
import { ChatInput } from './ChatInput';

function ConversationHeader() {
  const conversationId = useChatStore((s) => s.conversationId);
  const clearMessages = useChatStore((s) => s.clearMessages);

  return (
    <div className="flex items-center justify-between px-3 py-2 border-b shrink-0">
      <span className="text-sm text-muted-foreground font-mono truncate max-w-[200px]">
        {conversationId ? `${conversationId.slice(0, 8)}\u2026` : 'No conversation'}
      </span>
      <button
        type="button"
        onClick={clearMessages}
        className="text-xs text-muted-foreground hover:text-foreground shrink-0 ml-2"
      >
        Clear
      </button>
    </div>
  );
}

function ErrorBanner() {
  const error = useChatStore((s) => s.error);
  const setError = useChatStore((s) => s.setError);
  if (!error) return null;
  return (
    <div className="flex items-center justify-between px-3 py-2 bg-destructive/10 text-destructive text-sm shrink-0">
      <span>{error}</span>
      <button type="button" onClick={() => { setError(null); }} className="ml-2 text-xs hover:underline shrink-0">
        Dismiss
      </button>
    </div>
  );
}

export function ChatPanel() {
  const setChatConversationId = useChatStore((s) => s.setConversationId);
  const selectedAgent = useAppStore((s) => s.selectedAgent);
  const activeConversationId = useAppStore((s) => s.activeConversationId);
  const setActiveConversationId = useAppStore((s) => s.setActiveConversationId);
  const {
    sendMessage,
    startConversation,
    retryFromMessage,
    editAndResubmit,
    joinGlobalTraces,
    leaveGlobalTraces,
    connectionState,
  } = useAgentHub();
  const [conversationReady, setConversationReady] = useState(false);

  const handleRetry = (assistantMessageId: string): void => {
    if (!activeConversationId) return;
    useChatStore.getState().startStreaming();
    void retryFromMessage(activeConversationId, assistantMessageId).catch((err: unknown) => {
      useChatStore.getState().setError(err instanceof Error ? err.message : 'Retry failed');
    });
  };

  const handleEdit = (userMessageId: string, newContent: string): void => {
    if (!activeConversationId) return;
    useChatStore.getState().startStreaming();
    void editAndResubmit(activeConversationId, userMessageId, newContent).catch((err: unknown) => {
      useChatStore.getState().setError(err instanceof Error ? err.message : 'Edit failed');
    });
  };

  useEffect(() => {
    useHubActionsStore.getState().setActions({ joinGlobalTraces, leaveGlobalTraces });
    return () => { useHubActionsStore.getState().setActions(null); };
  }, [joinGlobalTraces, leaveGlobalTraces]);

  // Ensure a conversation id exists whenever an agent is selected. The header
  // dropdown is responsible for clearing the id when the user switches agents
  // so that sidebar-driven agent changes (click an old conversation) don't
  // clobber the selected conversation id.
  useEffect(() => {
    if (selectedAgent && !activeConversationId) {
      setActiveConversationId(crypto.randomUUID());
    }
  }, [selectedAgent, activeConversationId, setActiveConversationId]);

  // Keep the chat store's conversation id in sync with the active id from app state.
  useEffect(() => {
    if (activeConversationId) setChatConversationId(activeConversationId);
  }, [activeConversationId, setChatConversationId]);

  useEffect(() => {
    // StartConversation must wait for the SignalR connection to be ready —
    // invoking it mid-handshake silently drops the registration and later
    // SendMessage calls fail with "Conversation not found". ChatInput stays
    // disabled until the server acknowledges the conversation.
    if (connectionState !== 'connected' || !selectedAgent || !activeConversationId) return;
    setConversationReady(false);
    let cancelled = false;
    void startConversation(selectedAgent, activeConversationId)
      .then(() => { if (!cancelled) setConversationReady(true); })
      .catch((err: unknown) => {
        if (cancelled) return;
        useChatStore.getState().setError(
          err instanceof Error ? err.message : 'Failed to start conversation',
        );
      });
    return () => { cancelled = true; };
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [connectionState, selectedAgent, activeConversationId]);

  return (
    <div className="flex flex-col h-full">
      <ConversationHeader />
      <ErrorBanner />
      <div className="flex-1 overflow-hidden min-h-0">
        <MessageList
          onRetry={handleRetry}
          onEdit={handleEdit}
          disabled={!conversationReady}
        />
      </div>
      <TypingIndicator />
      {activeConversationId && (
        <ChatInput
          conversationId={activeConversationId}
          sendMessage={sendMessage}
          disabled={!conversationReady}
        />
      )}
    </div>
  );
}
