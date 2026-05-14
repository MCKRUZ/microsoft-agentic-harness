import { useEffect, useState } from 'react';
import { Settings, Sparkles } from 'lucide-react';
import { useQueryClient } from '@tanstack/react-query';
import { cn } from '@/lib/utils';
import { useChatStore } from './useChatStore';
import { useAppStore } from '@/stores/appStore';
import { useConversationSettingsStore } from '@/stores/conversationSettingsStore';
import { useAgentHub, type ConnectionState, type ConversationSettingsInput, type ServerConversationMessage } from '@/hooks/useAgentHub';
import { useAgentStream } from '@/hooks/useAgentStream';
import type { ChatMessage } from './useChatStore';
import { CONVERSATIONS_QUERY_KEY } from '@/features/conversations/useConversationsQuery';
import { MessageList } from './MessageList';
import { TypingIndicator } from './TypingIndicator';
import { ChatInput } from './ChatInput';
import { ConversationSettingsDrawer } from './ConversationSettingsDrawer';
import { Badge } from '@/components/ui/badge';
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip';
import { EmptyState } from '@/components/ui/empty-state';

const CONNECTION_LABELS: Record<ConnectionState, { text: string; color: string; dot: string }> = {
  connected: { text: 'Connected', color: 'text-emerald-400', dot: 'bg-emerald-400' },
  connecting: { text: 'Connecting...', color: 'text-yellow-400', dot: 'bg-yellow-400 animate-pulse' },
  reconnecting: { text: 'Reconnecting...', color: 'text-yellow-400', dot: 'bg-yellow-400 animate-pulse' },
  disconnected: { text: 'Disconnected', color: 'text-destructive', dot: 'bg-destructive' },
};

interface ConversationHeaderProps {
  onOpenSettings: () => void;
  canOpenSettings: boolean;
  connectionState: ConnectionState;
}

function ConversationHeader({ onOpenSettings, canOpenSettings, connectionState }: ConversationHeaderProps) {
  const conversationId = useChatStore((s) => s.conversationId);
  const clearMessages = useChatStore((s) => s.clearMessages);
  const connLabel = CONNECTION_LABELS[connectionState];

  return (
    <div className="flex items-center justify-between px-4 py-2.5 border-b border-border/50 shrink-0 bg-background/80 backdrop-blur-sm">
      <div className="flex items-center gap-3 min-w-0">
        {/* Connection status dot */}
        <Tooltip>
          <TooltipTrigger asChild>
            <div className="flex items-center gap-2">
              <span className={cn('w-2 h-2 rounded-full shrink-0', connLabel.dot)} />
              {connectionState !== 'connected' && (
                <span className={cn('text-xs font-medium', connLabel.color)}>{connLabel.text}</span>
              )}
            </div>
          </TooltipTrigger>
          <TooltipContent side="bottom" className="text-xs">{connLabel.text}</TooltipContent>
        </Tooltip>

        {conversationId && (
          <span className="text-xs text-muted-foreground font-mono truncate max-w-[180px] opacity-60">
            {conversationId.slice(0, 8)}
          </span>
        )}
      </div>
      <div className="flex items-center gap-1 shrink-0">
        <Tooltip>
          <TooltipTrigger asChild>
            <button
              type="button"
              onClick={onOpenSettings}
              disabled={!canOpenSettings}
              aria-label="Conversation settings"
              className={cn(
                'inline-flex items-center justify-center h-8 w-8 rounded-lg',
                'text-muted-foreground hover:text-foreground hover:bg-muted/60',
                'disabled:opacity-40 disabled:cursor-not-allowed transition-colors',
              )}
            >
              <Settings size={15} />
            </button>
          </TooltipTrigger>
          <TooltipContent side="bottom" className="text-xs">Settings</TooltipContent>
        </Tooltip>
        <button
          type="button"
          onClick={clearMessages}
          className={cn(
            'text-xs text-muted-foreground hover:text-foreground px-2 py-1 rounded-md',
            'hover:bg-muted/60 transition-colors',
          )}
        >
          Clear
        </button>
      </div>
    </div>
  );
}

function ErrorBanner() {
  const error = useChatStore((s) => s.error);
  const setError = useChatStore((s) => s.setError);
  if (!error) return null;
  return (
    <div className="flex items-center justify-between px-4 py-2.5 bg-destructive/10 border-b border-destructive/20 text-destructive text-sm shrink-0">
      <span className="text-xs font-medium">{error}</span>
      <button
        type="button"
        onClick={() => { setError(null); }}
        className="ml-2 text-xs font-medium hover:underline shrink-0 opacity-80 hover:opacity-100"
      >
        Dismiss
      </button>
    </div>
  );
}

export function ChatPanel() {
  const queryClient = useQueryClient();
  const setChatConversationId = useChatStore((s) => s.setConversationId);
  const selectedAgent = useAppStore((s) => s.selectedAgent);
  const activeConversationId = useAppStore((s) => s.activeConversationId);
  const setActiveConversationId = useAppStore((s) => s.setActiveConversationId);
  const {
    startConversation,
    retryFromMessage,
    editAndResubmit,
    setConversationSettings,
    connectionState,
  } = useAgentHub();
  const { sendMessage: agUiSend } = useAgentStream();
  const [conversationReady, setConversationReady] = useState(false);
  const [settingsOpen, setSettingsOpen] = useState(false);
  const currentSettings = useConversationSettingsStore((s) =>
    s.getSettings(activeConversationId),
  );
  const saveLocalSettings = useConversationSettingsStore((s) => s.setSettings);

  const handleSaveSettings = async (next: ConversationSettingsInput): Promise<void> => {
    if (!activeConversationId) return;
    await setConversationSettings(activeConversationId, next);
    saveLocalSettings(activeConversationId, next);
  };

  const handleRetry = (assistantMessageId: string): void => {
    if (!activeConversationId) return;
    useChatStore.getState().startStreaming();
    void retryFromMessage(activeConversationId, assistantMessageId).catch((err: unknown) => {
      useChatStore.getState().setError(err instanceof Error ? err.message : 'Retry failed');
    });
  };

  const sendMessage = async (conversationId: string, userMessageId: string, message: string): Promise<void> => {
    agUiSend(conversationId, userMessageId, message);
  };

  const handleSuggestionClick = (text: string): void => {
    if (!activeConversationId || !conversationReady) return;
    const userMessageId = crypto.randomUUID();
    useChatStore.getState().addMessage({
      id: userMessageId,
      role: 'user',
      content: text,
      timestamp: new Date(),
    });
    useChatStore.getState().startStreaming();
    agUiSend(activeConversationId, userMessageId, text);
  };

  const handleEdit = (userMessageId: string, newContent: string): void => {
    if (!activeConversationId) return;
    useChatStore.getState().startStreaming();
    void editAndResubmit(activeConversationId, userMessageId, newContent).catch((err: unknown) => {
      useChatStore.getState().setError(err instanceof Error ? err.message : 'Edit failed');
    });
  };

  useEffect(() => {
    if (selectedAgent && !activeConversationId) {
      setActiveConversationId(crypto.randomUUID());
    }
  }, [selectedAgent, activeConversationId, setActiveConversationId]);

  useEffect(() => {
    if (activeConversationId) setChatConversationId(activeConversationId);
  }, [activeConversationId, setChatConversationId]);

  useEffect(() => {
    if (connectionState !== 'connected' || !selectedAgent || !activeConversationId) return;
    setConversationReady(false);
    let cancelled = false;
    void startConversation(selectedAgent, activeConversationId)
      .then((history: ServerConversationMessage[]) => {
        if (cancelled) return;
        if (history?.length) {
          const mapped: ChatMessage[] = history
            .filter(m => {
              const r = m.role.toLowerCase();
              return r === 'user' || r === 'assistant';
            })
            .map(m => ({
              id: m.id,
              role: m.role.toLowerCase() as 'user' | 'assistant',
              content: m.content,
              timestamp: new Date(m.timestamp),
              toolCalls: m.toolCalls ?? undefined,
            }));
          if (mapped.length > 0) {
            useChatStore.getState().setMessages(mapped);
          }
        }
        void queryClient.invalidateQueries({ queryKey: CONVERSATIONS_QUERY_KEY });
        setConversationReady(true);
      })
      .catch((err: unknown) => {
        if (cancelled) return;
        useChatStore.getState().setError(
          err instanceof Error ? err.message : 'Failed to start conversation',
        );
        setConversationReady(true);
      });
    return () => { cancelled = true; };
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [connectionState, selectedAgent, activeConversationId]);

  if (!selectedAgent) {
    return (
      <div className="flex flex-col h-full items-center justify-center">
        <EmptyState
          icon={<Sparkles size={48} className="text-muted-foreground/40" />}
          title="No agent selected"
          description="Use the command palette (Ctrl+K) or select an agent from the sidebar to begin."
        />
      </div>
    );
  }

  return (
    <div className="flex flex-col h-full">
      <ConversationHeader
        onOpenSettings={() => { setSettingsOpen(true); }}
        canOpenSettings={conversationReady && activeConversationId !== null}
        connectionState={connectionState}
      />
      <ErrorBanner />
      <div className="flex-1 overflow-hidden min-h-0">
        <MessageList
          onRetry={handleRetry}
          onEdit={handleEdit}
          onSuggestionClick={handleSuggestionClick}
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
      <ConversationSettingsDrawer
        open={settingsOpen}
        onClose={() => { setSettingsOpen(false); }}
        initial={currentSettings}
        onSave={handleSaveSettings}
      />
    </div>
  );
}
