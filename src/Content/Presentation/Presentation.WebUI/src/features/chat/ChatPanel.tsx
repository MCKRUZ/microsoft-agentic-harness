import { useEffect, useRef } from 'react';
import { useChatStore } from './useChatStore';
import { useAppStore } from '@/stores/appStore';
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
  const setConversationId = useChatStore((s) => s.setConversationId);
  const conversationId = useChatStore((s) => s.conversationId);
  const selectedAgent = useAppStore((s) => s.selectedAgent);
  const { sendMessage, startConversation } = useAgentHub();

  // Initialize conversation on first mount
  useEffect(() => {
    if (!conversationId) {
      const newId = crypto.randomUUID();
      setConversationId(newId);
      if (selectedAgent) {
        void startConversation(selectedAgent, newId).catch(() => {
          // Connection may not be ready on first mount; user can retry by sending a message
        });
      }
    }
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Start a new conversation when the selected agent changes
  const isInitialMount = useRef(true);
  useEffect(() => {
    if (isInitialMount.current) {
      isInitialMount.current = false;
      return;
    }
    if (selectedAgent) {
      const newId = crypto.randomUUID();
      setConversationId(newId);
      void startConversation(selectedAgent, newId).catch(() => {});
    }
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [selectedAgent]);

  return (
    <div className="flex flex-col h-full">
      <ConversationHeader />
      <ErrorBanner />
      <div className="flex-1 overflow-hidden min-h-0">
        <MessageList />
      </div>
      <TypingIndicator />
      {conversationId && (
        <ChatInput conversationId={conversationId} sendMessage={sendMessage} />
      )}
    </div>
  );
}
