import { useState, type KeyboardEvent } from 'react';
import { Pencil, RotateCcw, Check, X, Copy } from 'lucide-react';
import { Markdown } from './Markdown';
import { Textarea } from '@/components/ui/textarea';
import { Button } from '@/components/ui/button';
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

interface MessageItemProps {
  message: ChatMessage;
  isStreaming?: boolean;
  onRetry?: (assistantMessageId: string) => void;
  onEdit?: (userMessageId: string, newContent: string) => void;
  disabled?: boolean;
}

export function MessageItem({
  message,
  isStreaming = false,
  onRetry,
  onEdit,
  disabled = false,
}: MessageItemProps) {
  const isUser = message.role === 'user';
  const [isEditing, setIsEditing] = useState(false);
  const [draft, setDraft] = useState(message.content);

  const canRetry = !isUser && !isStreaming && onRetry != null && !disabled;
  const canEdit = isUser && !isStreaming && onEdit != null && !disabled;
  const canCopy = !isStreaming && !disabled && message.content.length > 0;
  const [copied, setCopied] = useState(false);

  const handleCopy = async (): Promise<void> => {
    try {
      await navigator.clipboard.writeText(message.content);
      setCopied(true);
      window.setTimeout(() => { setCopied(false); }, 1500);
    } catch {
      /* clipboard unavailable — silent. */
    }
  };

  const handleSaveEdit = (): void => {
    const trimmed = draft.trim();
    if (!trimmed || trimmed === message.content) {
      setIsEditing(false);
      setDraft(message.content);
      return;
    }
    onEdit?.(message.id, trimmed);
    setIsEditing(false);
  };

  const handleCancelEdit = (): void => {
    setIsEditing(false);
    setDraft(message.content);
  };

  const handleKeyDown = (e: KeyboardEvent<HTMLTextAreaElement>): void => {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      handleSaveEdit();
    } else if (e.key === 'Escape') {
      e.preventDefault();
      handleCancelEdit();
    }
  };

  return (
    <div className={`group flex ${isUser ? 'justify-end' : 'justify-start'} px-3 py-1`}>
      <div
        className={
          isUser
            ? 'ml-auto bg-primary text-primary-foreground rounded-lg p-3 max-w-[80%]'
            : 'mr-auto bg-muted rounded-lg p-3 max-w-[80%]'
        }
      >
        {isEditing ? (
          <div className="flex flex-col gap-2 min-w-[240px]">
            <Textarea
              value={draft}
              onChange={(e) => { setDraft(e.target.value); }}
              onKeyDown={handleKeyDown}
              rows={3}
              aria-label="Edit message"
              autoFocus
            />
            <div className="flex justify-end gap-2">
              <Button type="button" variant="ghost" size="sm" onClick={handleCancelEdit}>
                <X size={14} className="mr-1" /> Cancel
              </Button>
              <Button type="button" size="sm" onClick={handleSaveEdit}>
                <Check size={14} className="mr-1" /> Save
              </Button>
            </div>
          </div>
        ) : isUser ? (
          <p className="whitespace-pre-wrap">{message.content}</p>
        ) : (
          <Markdown content={message.content} />
        )}
        {isStreaming && (
          <span
            className="inline-block w-1.5 h-4 bg-current animate-pulse ml-0.5 align-middle"
            aria-hidden
          />
        )}
        {(message.toolCalls ?? []).map((tc, i) => (
          <ToolCallChip key={i} toolCall={tc} />
        ))}
        {!isEditing && (canRetry || canEdit || canCopy) && (
          <div className="flex gap-1 mt-2 opacity-0 group-hover:opacity-100 transition-opacity">
            {canCopy && (
              <button
                type="button"
                onClick={() => { void handleCopy(); }}
                aria-label={copied ? 'Copied' : 'Copy message'}
                title={copied ? 'Copied' : 'Copy'}
                className={`inline-flex items-center gap-1 text-xs px-2 py-0.5 rounded ${isUser ? 'hover:bg-primary-foreground/20' : 'hover:bg-muted-foreground/20'}`}
              >
                {copied ? <Check size={12} /> : <Copy size={12} />} {copied ? 'Copied' : 'Copy'}
              </button>
            )}
            {canRetry && (
              <button
                type="button"
                onClick={() => onRetry?.(message.id)}
                aria-label="Retry response"
                title="Regenerate"
                className="inline-flex items-center gap-1 text-xs px-2 py-0.5 rounded hover:bg-muted-foreground/20"
              >
                <RotateCcw size={12} /> Regenerate
              </button>
            )}
            {canEdit && (
              <button
                type="button"
                onClick={() => { setDraft(message.content); setIsEditing(true); }}
                aria-label="Edit message"
                title="Edit"
                className="inline-flex items-center gap-1 text-xs px-2 py-0.5 rounded hover:bg-primary-foreground/20"
              >
                <Pencil size={12} /> Edit
              </button>
            )}
          </div>
        )}
      </div>
    </div>
  );
}
