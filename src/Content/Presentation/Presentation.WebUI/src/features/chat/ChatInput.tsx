import { useMemo, useRef, useState, type KeyboardEvent, type ChangeEvent, type DragEvent } from 'react';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { Paperclip, X } from 'lucide-react';
import { useChatStore } from './useChatStore';
import { Button } from '@/components/ui/button';
import { Textarea } from '@/components/ui/textarea';
import { MentionPicker, type MentionItem } from './MentionPicker';
import { usePromptsQuery, useToolsQuery } from '@/features/mcp/useMcpQuery';

const MAX_ATTACHMENT_BYTES = 500 * 1024;
const MAX_MESSAGE_CHARS = 40_000;
const ACCEPTED_EXTENSIONS = '.txt,.md,.json,.csv,.log,.yaml,.yml,.xml,.html,.css,.js,.ts,.tsx,.jsx,.py,.cs,.java,.go,.rs';

const schema = z.object({
  message: z.string().min(1, 'Message is required').max(MAX_MESSAGE_CHARS, 'Message too long'),
});

type FormData = z.infer<typeof schema>;

interface Attachment {
  name: string;
  content: string;
  size: number;
}

interface ChatInputProps {
  conversationId: string;
  sendMessage: (conversationId: string, userMessageId: string, message: string) => Promise<void>;
  disabled?: boolean;
}

type TriggerChar = '@' | '/';

interface TriggerState {
  char: TriggerChar;
  start: number;
  filter: string;
}

function composeMessage(message: string, attachment: Attachment | null): string {
  if (!attachment) return message;
  return `[Attached: ${attachment.name}]\n\`\`\`\n${attachment.content}\n\`\`\`\n\n${message}`;
}

/**
 * Detect an active @/ picker trigger at the cursor. A trigger is valid when the char is
 * preceded by start-of-text or whitespace and everything up to the cursor is non-whitespace.
 */
function detectTrigger(value: string, caret: number): TriggerState | null {
  for (let i = caret - 1; i >= 0; i--) {
    const ch = value[i];
    if (ch === '@' || ch === '/') {
      const prev = i === 0 ? ' ' : value[i - 1];
      if (!/\s/.test(prev) && i !== 0) return null;
      return { char: ch, start: i, filter: value.slice(i + 1, caret) };
    }
    if (/\s/.test(ch)) return null;
  }
  return null;
}

export function ChatInput({ conversationId, sendMessage, disabled = false }: ChatInputProps) {
  const isStreaming = useChatStore((s) => s.isStreaming);
  const addMessage = useChatStore((s) => s.addMessage);
  const startStreaming = useChatStore((s) => s.startStreaming);
  const [attachment, setAttachment] = useState<Attachment | null>(null);
  const [attachmentError, setAttachmentError] = useState<string | null>(null);
  const [isDragOver, setIsDragOver] = useState(false);
  const dragCounterRef = useRef(0);
  const fileInputRef = useRef<HTMLInputElement>(null);
  const textareaRef = useRef<HTMLTextAreaElement | null>(null);
  const [trigger, setTrigger] = useState<TriggerState | null>(null);
  const [pickerIndex, setPickerIndex] = useState(0);
  const dismissedStartRef = useRef<number | null>(null);

  const promptsQuery = usePromptsQuery();
  const toolsQuery = useToolsQuery();

  const form = useForm<FormData>({
    resolver: zodResolver(schema),
    defaultValues: { message: '' },
  });

  const watchedMessage = form.watch('message');
  const { ref: registerRef, ...registerRest } = form.register('message');

  const pickerSource: MentionItem[] = useMemo(() => {
    if (!trigger) return [];
    if (trigger.char === '@') {
      return (promptsQuery.data ?? []).map((p) => ({ name: p.name, description: p.description }));
    }
    return (toolsQuery.data ?? []).map((t) => ({ name: t.name, description: t.description }));
  }, [trigger, promptsQuery.data, toolsQuery.data]);

  const filteredItems = useMemo(() => {
    if (!trigger) return [];
    const f = trigger.filter.toLowerCase();
    if (!f) return pickerSource;
    return pickerSource.filter((i) => i.name.toLowerCase().includes(f));
  }, [trigger, pickerSource]);

  const pickerLoading =
    trigger?.char === '@' ? promptsQuery.isLoading : trigger?.char === '/' ? toolsQuery.isLoading : false;

  const updateTrigger = (value: string, caret: number): void => {
    const next = detectTrigger(value, caret);
    if (next && dismissedStartRef.current === next.start) {
      setTrigger(null);
      return;
    }
    if (!next) dismissedStartRef.current = null;
    setTrigger(next);
    setPickerIndex(0);
  };

  const handleChange = (e: ChangeEvent<HTMLTextAreaElement>): void => {
    void registerRest.onChange(e);
    updateTrigger(e.target.value, e.target.selectionStart ?? e.target.value.length);
  };

  const handleSelectionUpdate = (): void => {
    const el = textareaRef.current;
    if (!el) return;
    updateTrigger(el.value, el.selectionStart ?? el.value.length);
  };

  const applyMention = (item: MentionItem): void => {
    const el = textareaRef.current;
    if (!el || !trigger) return;
    const before = el.value.slice(0, trigger.start);
    const after = el.value.slice(el.selectionStart ?? el.value.length);
    const insertion = `${trigger.char}${item.name} `;
    const nextValue = `${before}${insertion}${after}`;
    form.setValue('message', nextValue, { shouldValidate: true, shouldDirty: true });
    setTrigger(null);
    const nextCaret = before.length + insertion.length;
    requestAnimationFrame(() => {
      el.focus();
      el.setSelectionRange(nextCaret, nextCaret);
    });
  };

  const handleAttachClick = (): void => {
    setAttachmentError(null);
    fileInputRef.current?.click();
  };

  const readFile = (file: File): void => {
    if (file.size > MAX_ATTACHMENT_BYTES) {
      setAttachmentError(`File too large (max ${(MAX_ATTACHMENT_BYTES / 1024).toFixed(0)}KB).`);
      return;
    }
    const reader = new FileReader();
    reader.onerror = () => { setAttachmentError('Failed to read file.'); };
    reader.onload = () => {
      const content = typeof reader.result === 'string' ? reader.result : '';
      setAttachment({ name: file.name, content, size: file.size });
      setAttachmentError(null);
    };
    reader.readAsText(file);
  };

  const handleFileChange = (e: ChangeEvent<HTMLInputElement>): void => {
    const file = e.target.files?.[0];
    e.target.value = '';
    if (!file) return;
    readFile(file);
  };

  const handleDragEnter = (e: DragEvent<HTMLFormElement>): void => {
    if (disabled || isStreaming) return;
    e.preventDefault();
    dragCounterRef.current += 1;
    if (e.dataTransfer.types.includes('Files')) setIsDragOver(true);
  };

  const handleDragOver = (e: DragEvent<HTMLFormElement>): void => {
    if (disabled || isStreaming) return;
    e.preventDefault();
    e.dataTransfer.dropEffect = 'copy';
  };

  const handleDragLeave = (e: DragEvent<HTMLFormElement>): void => {
    e.preventDefault();
    dragCounterRef.current -= 1;
    if (dragCounterRef.current <= 0) {
      dragCounterRef.current = 0;
      setIsDragOver(false);
    }
  };

  const handleDrop = (e: DragEvent<HTMLFormElement>): void => {
    e.preventDefault();
    dragCounterRef.current = 0;
    setIsDragOver(false);
    if (disabled || isStreaming) return;
    const file = e.dataTransfer.files?.[0];
    if (file) readFile(file);
  };

  const clearAttachment = (): void => {
    setAttachment(null);
    setAttachmentError(null);
  };

  const onSubmit = async (data: FormData): Promise<void> => {
    if (disabled || isStreaming) return;
    const composed = composeMessage(data.message, attachment);
    const userMessageId = crypto.randomUUID();
    addMessage({
      id: userMessageId,
      role: 'user',
      content: composed,
      timestamp: new Date(),
    });
    startStreaming();
    form.reset();
    clearAttachment();
    setTrigger(null);
    try {
      await sendMessage(conversationId, userMessageId, composed);
    } catch (err) {
      useChatStore.getState().setError(err instanceof Error ? err.message : 'Failed to send message');
    }
  };

  const handleKeyDown = (e: KeyboardEvent<HTMLTextAreaElement>): void => {
    if (trigger && filteredItems.length > 0) {
      if (e.key === 'ArrowDown') {
        e.preventDefault();
        setPickerIndex((i) => (i + 1) % filteredItems.length);
        return;
      }
      if (e.key === 'ArrowUp') {
        e.preventDefault();
        setPickerIndex((i) => (i - 1 + filteredItems.length) % filteredItems.length);
        return;
      }
      if (e.key === 'Enter' || e.key === 'Tab') {
        e.preventDefault();
        applyMention(filteredItems[pickerIndex]);
        return;
      }
      if (e.key === 'Escape') {
        e.preventDefault();
        dismissedStartRef.current = trigger.start;
        setTrigger(null);
        return;
      }
    }
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      void form.handleSubmit(onSubmit)();
    }
  };

  const showPicker = trigger !== null;

  return (
    <form
      onSubmit={(e) => { void form.handleSubmit(onSubmit)(e); }}
      onDragEnter={handleDragEnter}
      onDragOver={handleDragOver}
      onDragLeave={handleDragLeave}
      onDrop={handleDrop}
      className={`p-3 border-t flex flex-col gap-2 shrink-0 relative ${isDragOver ? 'ring-2 ring-primary ring-inset bg-primary/5' : ''}`}
    >
      {attachment && (
        <div className="flex items-center gap-2 text-xs px-2 py-1 border rounded bg-muted/50 self-start max-w-full">
          <Paperclip size={12} className="shrink-0 text-muted-foreground" />
          <span className="truncate">{attachment.name}</span>
          <span className="text-muted-foreground shrink-0">({(attachment.size / 1024).toFixed(1)}KB)</span>
          <button
            type="button"
            onClick={clearAttachment}
            aria-label={`Remove attachment ${attachment.name}`}
            className="text-muted-foreground hover:text-destructive shrink-0"
          >
            <X size={12} />
          </button>
        </div>
      )}
      {attachmentError && <p className="text-xs text-destructive">{attachmentError}</p>}
      <div className="relative">
        {showPicker && trigger && (
          <MentionPicker
            items={filteredItems}
            activeIndex={pickerIndex}
            onSelect={applyMention}
            onHover={setPickerIndex}
            trigger={trigger.char}
            loading={pickerLoading}
          />
        )}
        <Textarea
          {...registerRest}
          ref={(el) => {
            registerRef(el);
            textareaRef.current = el;
          }}
          onChange={handleChange}
          onKeyUp={handleSelectionUpdate}
          onClick={handleSelectionUpdate}
          disabled={isStreaming || disabled}
          onKeyDown={handleKeyDown}
          placeholder="Type a message... (@ for prompts, / for tools, Shift+Enter for newline)"
          rows={3}
        />
      </div>
      {form.formState.errors.message && (
        <p className="text-sm text-destructive">{form.formState.errors.message.message}</p>
      )}
      <div className="flex justify-between items-center">
        <div className="flex items-center gap-2">
          <input
            ref={fileInputRef}
            type="file"
            accept={ACCEPTED_EXTENSIONS}
            onChange={handleFileChange}
            className="hidden"
            aria-hidden="true"
            tabIndex={-1}
          />
          <button
            type="button"
            onClick={handleAttachClick}
            disabled={isStreaming || disabled}
            title="Attach a text file"
            aria-label="Attach file"
            className="inline-flex items-center justify-center h-8 w-8 rounded text-muted-foreground hover:bg-muted disabled:opacity-50 disabled:cursor-not-allowed"
          >
            <Paperclip className="h-4 w-4" />
          </button>
          <span className="text-xs text-muted-foreground">{watchedMessage.length}/{MAX_MESSAGE_CHARS.toLocaleString()}</span>
        </div>
        <Button type="submit" disabled={isStreaming || disabled}>
          Send
        </Button>
      </div>
    </form>
  );
}
