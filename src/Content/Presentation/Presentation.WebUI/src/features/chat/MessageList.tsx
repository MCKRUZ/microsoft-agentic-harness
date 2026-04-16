import { useRef, useEffect } from 'react';
import { useChatStore } from './useChatStore';
import { MessageItem } from './MessageItem';

export function MessageList() {
  const messages = useChatStore((s) => s.messages);
  const isStreaming = useChatStore((s) => s.isStreaming);
  const streamingContent = useChatStore((s) => s.streamingContent);
  const bottomRef = useRef<HTMLDivElement>(null);

  const hasStreamingItem = isStreaming && streamingContent.length > 0;

  useEffect(() => {
    bottomRef.current?.scrollIntoView({ behavior: 'smooth' });
  }, [messages.length, streamingContent]);

  return (
    <div className="flex flex-col gap-1 h-full overflow-y-auto">
      {messages.map((message) => (
        <MessageItem key={message.id} message={message} />
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
