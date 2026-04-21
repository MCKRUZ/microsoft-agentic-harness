import { useState, type KeyboardEvent } from 'react';
import { Pencil, RotateCcw, Check, X, Copy, Bot, User, ChevronDown } from 'lucide-react';
import { cn } from '@/lib/utils';
import { Markdown } from './Markdown';
import { Textarea } from '@/components/ui/textarea';
import { Button } from '@/components/ui/button';
import { Badge } from '@/components/ui/badge';
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from '@/components/ui/collapsible';
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip';
import { JsonViewer } from '@/components/ui/json-viewer';
import type { ChatMessage, ToolCallSummary } from './useChatStore';

function ToolCallCard({ toolCall }: { toolCall: ToolCallSummary }) {
  const hasOutput = toolCall.output !== undefined && toolCall.output !== null;
  const statusVariant = hasOutput ? 'secondary' as const : 'outline' as const;
  const statusText = hasOutput ? 'completed' : 'pending';

  return (
    <Collapsible>
      <div className="rounded-lg border border-border/50 bg-card/50 overflow-hidden mt-2">
        <CollapsibleTrigger asChild>
          <button
            type="button"
            className="flex items-center gap-2 w-full px-3 py-2 text-left text-xs hover:bg-muted/50 transition-colors"
          >
            <ChevronDown
              size={12}
              className="text-muted-foreground transition-transform [[data-state=closed]_&]:rotate-[-90deg]"
            />
            <span className="font-mono font-medium text-foreground/90">{toolCall.toolName}</span>
            <Badge variant={statusVariant} className="ml-auto text-[10px] px-1.5 py-0">
              {statusText}
            </Badge>
          </button>
        </CollapsibleTrigger>
        <CollapsibleContent>
          <div className="border-t border-border/50 px-3 py-2 space-y-2">
            {toolCall.input && Object.keys(toolCall.input).length > 0 && (
              <div>
                <span className="text-[10px] font-medium text-muted-foreground uppercase tracking-wider">Input</span>
                <JsonViewer data={toolCall.input} maxInitialDepth={1} className="mt-1" />
              </div>
            )}
            {hasOutput && (
              <div>
                <span className="text-[10px] font-medium text-muted-foreground uppercase tracking-wider">Output</span>
                <JsonViewer data={toolCall.output} maxInitialDepth={1} className="mt-1" />
              </div>
            )}
          </div>
        </CollapsibleContent>
      </div>
    </Collapsible>
  );
}

interface ActionButtonProps {
  onClick: () => void;
  icon: React.ReactNode;
  label: string;
}

function ActionButton({ onClick, icon, label }: ActionButtonProps) {
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <button
          type="button"
          onClick={onClick}
          aria-label={label}
          className={cn(
            'inline-flex items-center justify-center h-7 w-7 rounded-md',
            'text-muted-foreground hover:text-foreground hover:bg-muted/80',
            'transition-all duration-150',
          )}
        >
          {icon}
        </button>
      </TooltipTrigger>
      <TooltipContent side="bottom" className="text-xs">
        {label}
      </TooltipContent>
    </Tooltip>
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

  const hasActions = canRetry || canEdit || canCopy;

  return (
    <div
      className={cn(
        'group flex gap-3 px-4 py-4 md:px-6',
        isUser ? 'flex-row-reverse' : 'flex-row',
      )}
    >
      {/* Avatar */}
      <div
        className={cn(
          'flex-shrink-0 flex items-center justify-center w-8 h-8 rounded-full mt-0.5',
          isUser
            ? 'bg-primary text-primary-foreground'
            : 'bg-muted border border-border/50 text-muted-foreground',
        )}
      >
        {isUser ? <User size={14} /> : <Bot size={14} />}
      </div>

      {/* Content */}
      <div className={cn('flex flex-col min-w-0', isUser ? 'items-end' : 'items-start', 'max-w-[85%] md:max-w-[75%]')}>
        {/* Role label */}
        <span className="text-[11px] font-medium text-muted-foreground mb-1 px-1">
          {isUser ? 'You' : 'Assistant'}
        </span>

        {/* Message bubble */}
        <div
          className={cn(
            'rounded-2xl px-4 py-3 text-sm leading-relaxed',
            isUser
              ? 'bg-primary text-primary-foreground rounded-tr-sm'
              : 'bg-card border border-border/50 text-card-foreground rounded-tl-sm',
          )}
        >
          {isEditing ? (
            <div className="flex flex-col gap-2 min-w-[280px]">
              <Textarea
                value={draft}
                onChange={(e) => { setDraft(e.target.value); }}
                onKeyDown={handleKeyDown}
                rows={3}
                aria-label="Edit message"
                autoFocus
                className="bg-background/50 border-border/50"
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
              className="inline-block w-0.5 h-4 bg-current animate-[cursor-blink_1s_ease-in-out_infinite] ml-0.5 align-middle rounded-full"
              aria-hidden
            />
          )}
        </div>

        {/* Tool calls — outside the bubble for cleaner layout */}
        {!isUser && (message.toolCalls ?? []).length > 0 && (
          <div className="w-full mt-1 space-y-1">
            {(message.toolCalls ?? []).map((tc, i) => (
              <ToolCallCard key={i} toolCall={tc} />
            ))}
          </div>
        )}

        {/* Action buttons — show on hover */}
        {!isEditing && hasActions && (
          <div
            className={cn(
              'flex items-center gap-0.5 mt-1 px-1',
              'opacity-0 group-hover:opacity-100 focus-within:opacity-100 transition-opacity duration-150',
            )}
          >
            {canCopy && (
              <ActionButton
                onClick={() => { void handleCopy(); }}
                icon={copied ? <Check size={14} className="text-emerald-400" /> : <Copy size={14} />}
                label={copied ? 'Copied' : 'Copy message'}
              />
            )}
            {canRetry && (
              <ActionButton
                onClick={() => onRetry?.(message.id)}
                icon={<RotateCcw size={14} />}
                label="Regenerate response"
              />
            )}
            {canEdit && (
              <ActionButton
                onClick={() => { setDraft(message.content); setIsEditing(true); }}
                icon={<Pencil size={14} />}
                label="Edit message"
              />
            )}
          </div>
        )}
      </div>
    </div>
  );
}
