import { useRef, useEffect, useState, useCallback } from 'react';
import { ArrowDown } from 'lucide-react';
import { useChatStore } from './useChatStore';
import { MessageItem } from './MessageItem';

interface MessageListProps {
  onRetry?: (assistantMessageId: string) => void;
  onEdit?: (userMessageId: string, newContent: string) => void;
  disabled?: boolean;
}

/** Pixels from the bottom we still treat as "at bottom" — covers rounding + sub-pixel scroll. */
const AT_BOTTOM_THRESHOLD = 24;

export function MessageList({ onRetry, onEdit, disabled = false }: MessageListProps = {}) {
  const messages = useChatStore((s) => s.messages);
  const isStreaming = useChatStore((s) => s.isStreaming);
  const streamingContent = useChatStore((s) => s.streamingContent);
  const scrollRef = useRef<HTMLDivElement>(null);
  const bottomRef = useRef<HTMLDivElement>(null);
  const [atBottom, setAtBottom] = useState(true);

  const hasStreamingItem = isStreaming && streamingContent.length > 0;
  const actionsDisabled = disabled || isStreaming;

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

  return (
    <div className="relative h-full">
      <div
        ref={scrollRef}
        onScroll={handleScroll}
        className="flex flex-col gap-1 h-full overflow-y-auto"
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
          className="absolute bottom-3 left-1/2 -translate-x-1/2 z-10 flex items-center gap-1 rounded-full border bg-background/90 px-3 py-1.5 text-xs text-foreground shadow hover:bg-accent"
        >
          <ArrowDown size={14} />
          <span>Jump to latest</span>
        </button>
      )}
    </div>
  );
}
