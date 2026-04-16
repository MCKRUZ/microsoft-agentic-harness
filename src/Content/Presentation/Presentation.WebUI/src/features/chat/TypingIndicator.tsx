import { useChatStore } from './useChatStore';

export function TypingIndicator() {
  const isStreaming = useChatStore((s) => s.isStreaming);
  if (!isStreaming) return null;

  return (
    <div className="flex gap-1 px-3 py-2 shrink-0" aria-label="Agent is typing">
      {[0, 150, 300].map((delay) => (
        <span
          key={delay}
          className="w-2 h-2 rounded-full bg-muted-foreground animate-bounce"
          style={{ animationDelay: `${delay}ms` }}
        />
      ))}
    </div>
  );
}
