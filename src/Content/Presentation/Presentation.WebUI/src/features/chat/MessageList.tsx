import { useRef, useEffect, useState, useCallback } from 'react';
import { ArrowDown, Sparkles, MessageCircle } from 'lucide-react';
import { cn } from '@/lib/utils';
import { useChatStore } from './useChatStore';
import { MessageItem } from './MessageItem';
import { EmptyState } from '@/components/ui/empty-state';

interface MessageListProps {
  onRetry?: (assistantMessageId: string) => void;
  onEdit?: (userMessageId: string, newContent: string) => void;
  onSuggestionClick?: (text: string) => void;
  disabled?: boolean;
}

/** Pixels from the bottom we still treat as "at bottom" — covers rounding + sub-pixel scroll. */
const AT_BOTTOM_THRESHOLD = 24;

function ChatEmptyState({ onSuggestionClick }: { onSuggestionClick?: (text: string) => void }) {
  return (
    <div className="flex flex-col items-center justify-center h-full px-4">
      <EmptyState
        icon={<Sparkles size={40} className="text-muted-foreground/50" />}
        title="Start a conversation"
        description="Send a message to begin chatting with the agent. Use @ to reference prompts or / to invoke tools."
        className="py-0"
      />
      <div className="mt-8 grid grid-cols-1 sm:grid-cols-2 gap-2 max-w-md w-full">
        {[
          'What can you help me with?',
          'Explain your available tools',
          'Walk me through your capabilities',
          'Help me get started',
        ].map((suggestion) => (
          <button
            key={suggestion}
            type="button"
            className={cn(
              'text-left text-xs px-3 py-2.5 rounded-lg border border-border/50',
              'bg-card/50 text-muted-foreground hover:text-foreground hover:bg-muted/60',
              'transition-all duration-150 hover:border-border',
            )}
            onClick={() => onSuggestionClick?.(suggestion)}
          >
            <MessageCircle size={12} className="inline mr-1.5 opacity-50" />
            {suggestion}
          </button>
        ))}
      </div>
    </div>
  );
}

export function MessageList({ onRetry, onEdit, onSuggestionClick, disabled = false }: MessageListProps) {
  const messages = useChatStore((s) => s.messages);
  const isStreaming = useChatStore((s) => s.isStreaming);
  const streamingContent = useChatStore((s) => s.streamingContent);
  const scrollRef = useRef<HTMLDivElement>(null);
  const bottomRef = useRef<HTMLDivElement>(null);
  const [atBottom, setAtBottom] = useState(true);

  const hasStreamingItem = isStreaming && streamingContent.length > 0;
  const actionsDisabled = disabled || isStreaming;
  const isEmpty = messages.length === 0 && !hasStreamingItem;

  const scrollToBottom = useCallback((smooth = true): void => {
    bottomRef.current?.scrollIntoView({ behavior: smooth ? 'smooth' : 'auto' });
  }, []);

  // Recompute whether we're at the bottom whenever the user scrolls.
  const handleScroll = useCallback((): void => {
    const el = scrollRef.current;
    if (!el) return;
    const distance = el.scrollHeight - el.scrollTop - el.clientHeight;
    setAtBottom(distance <= AT_BOTTOM_THRESHOLD);
  }, []);

  // Only auto-follow when the user hasn't scrolled away from the bottom.
  useEffect(() => {
    if (atBottom) scrollToBottom(true);
  }, [messages.length, streamingContent, atBottom, scrollToBottom]);

  if (isEmpty) {
    return <ChatEmptyState onSuggestionClick={onSuggestionClick} />;
  }

  return (
    <div className="relative h-full">
      <div
        ref={scrollRef}
        onScroll={handleScroll}
        className="flex flex-col h-full overflow-y-auto py-4"
      >
        {messages.map((message) => (
          <MessageItem
            key={message.id}
            message={message}
            onRetry={onRetry}
            onEdit={onEdit}
            disabled={actionsDisabled}
          />
        ))}
        {hasStreamingItem && (
          <MessageItem
            message={{ id: 'streaming', role: 'assistant', content: streamingContent, timestamp: new Date() }}
            isStreaming
          />
        )}
        <div ref={bottomRef} aria-hidden />
      </div>

      {!atBottom && (
        <button
          type="button"
          onClick={() => { scrollToBottom(true); }}
          aria-label="Scroll to bottom"
          title="Scroll to bottom"
          className={cn(
            'absolute bottom-4 left-1/2 -translate-x-1/2 z-10',
            'flex items-center gap-1.5 rounded-full border border-border/80',
            'bg-background/95 backdrop-blur-sm px-3 py-1.5 text-xs text-foreground',
            'shadow-lg hover:bg-accent transition-colors',
          )}
        >
          <ArrowDown size={14} />
          <span>New messages</span>
        </button>
      )}
    </div>
  );
}
