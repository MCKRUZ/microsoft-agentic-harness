import { useState } from 'react';
import type { ReactNode } from 'react';
import type { ChatMessage, ToolCallSummary } from './useChatStore';

function ToolCallChip({ toolCall }: { toolCall: ToolCallSummary }) {
  const [expanded, setExpanded] = useState(false);
  return (
    <div className="mt-1">
      <button
        type="button"
        onClick={() => { setExpanded(!expanded); }}
        className="text-xs bg-muted-foreground/20 rounded px-2 py-0.5 hover:bg-muted-foreground/30"
      >
        {toolCall.toolName}
      </button>
      {expanded && (
        <pre className="text-xs mt-1 p-2 bg-muted rounded overflow-auto max-h-40">
          {JSON.stringify({ input: toolCall.input, output: toolCall.output }, null, 2)}
        </pre>
      )}
    </div>
  );
}

// Security: content is rendered via React JSX which auto-escapes all strings.
// If a markdown renderer is added in future, sanitize with DOMPurify first.
function parseContent(content: string): ReactNode {
  const parts: ReactNode[] = [];
  const codeRegex = /```[\w]*\n([\s\S]*?)```/g;
  let lastIndex = 0;
  let match: RegExpExecArray | null;

  while ((match = codeRegex.exec(content)) !== null) {
    const before = content.slice(lastIndex, match.index);
    if (before) {
      parts.push(<p key={`text-${lastIndex}`} className="whitespace-pre-wrap">{before}</p>);
    }
    const codeContent = match[1] ?? '';
    const matchStart = match.index;
    parts.push(
      <pre key={`code-${matchStart}`} className="bg-muted rounded p-2 my-1 overflow-auto text-sm">
        <code>{codeContent}</code>
      </pre>,
    );
    lastIndex = match.index + (match[0]?.length ?? 0);
  }

  const remaining = content.slice(lastIndex);
  if (remaining || parts.length === 0) {
    parts.push(<p key={`text-end-${lastIndex}`} className="whitespace-pre-wrap">{remaining}</p>);
  }

  return <>{parts}</>;
}

interface MessageItemProps {
  message: ChatMessage;
  isStreaming?: boolean;
}

export function MessageItem({ message, isStreaming = false }: MessageItemProps) {
  const isUser = message.role === 'user';

  return (
    <div className={`flex ${isUser ? 'justify-end' : 'justify-start'} px-3 py-1`}>
      <div
        className={
          isUser
            ? 'ml-auto bg-primary text-primary-foreground rounded-lg p-3 max-w-[80%]'
            : 'mr-auto bg-muted rounded-lg p-3 max-w-[80%]'
        }
      >
        {parseContent(message.content)}
        {isStreaming && (
          <span
            className="inline-block w-1.5 h-4 bg-current animate-pulse ml-0.5 align-middle"
            aria-hidden
          />
        )}
        {(message.toolCalls ?? []).map((tc, i) => (
          <ToolCallChip key={i} toolCall={tc} />
        ))}
      </div>
    </div>
  );
}
