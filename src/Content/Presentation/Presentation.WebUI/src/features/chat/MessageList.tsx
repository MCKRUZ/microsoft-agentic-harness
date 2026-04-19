import { useRef, useEffect } from 'react';
import { useChatStore } from './useChatStore';
import { MessageItem } from './MessageItem';

interface MessageListProps {
  onRetry?: (assistantMessageId: string) => void;
  onEdit?: (userMessageId: string, newContent: string) => void;
  disabled?: boolean;
}

export function MessageList({ onRetry, onEdit, disabled = false }: MessageListProps = {}) {
  const messages = useChatStore((s) => s.messages);
  const isStreaming = useChatStore((s) => s.isStreaming);
  const streamingContent = useChatStore((s) => s.streamingContent);
  const bottomRef = useRef<HTMLDivElement>(null);

  const hasStreamingItem = isStreaming && streamingContent.length > 0;
  const actionsDisabled = disabled || isStreaming;

  useEffect(() => {
    bottomRef.current?.scrollIntoView({ behavior: 'smooth' });
  }, [messages.length, streamingContent]);

  return (
    <div className="flex flex-col gap-1 h-full overflow-y-auto">
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
  );
}
