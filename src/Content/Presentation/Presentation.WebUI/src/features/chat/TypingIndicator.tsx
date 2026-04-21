import { useChatStore } from './useChatStore';

export function TypingIndicator() {
  const isStreaming = useChatStore((s) => s.isStreaming);
  if (!isStreaming) return null;

  return (
    <div className="flex items-center gap-2 px-6 py-3 shrink-0" aria-label="Agent is typing">
      <div className="flex items-center gap-1.5 px-3 py-2 rounded-xl bg-muted/60">
        {[0, 150, 300].map((delay) => (
          <span
            key={delay}
            className="w-1.5 h-1.5 rounded-full bg-muted-foreground/70 animate-[typing-pulse_1.4s_ease-in-out_infinite]"
            style={{ animationDelay: `${delay}ms` }}
          />
        ))}
      </div>
    </div>
  );
}
