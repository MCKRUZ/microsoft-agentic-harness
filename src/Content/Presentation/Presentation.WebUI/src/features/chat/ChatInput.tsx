import { useRef, useState, type KeyboardEvent, type ChangeEvent } from 'react';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { Paperclip, X } from 'lucide-react';
import { useChatStore } from './useChatStore';
import { Button } from '@/components/ui/button';
import { Textarea } from '@/components/ui/textarea';

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

function composeMessage(message: string, attachment: Attachment | null): string {
  if (!attachment) return message;
  return `[Attached: ${attachment.name}]\n\`\`\`\n${attachment.content}\n\`\`\`\n\n${message}`;
}

export function ChatInput({ conversationId, sendMessage, disabled = false }: ChatInputProps) {
  const isStreaming = useChatStore((s) => s.isStreaming);
  const addMessage = useChatStore((s) => s.addMessage);
  const startStreaming = useChatStore((s) => s.startStreaming);
  const [attachment, setAttachment] = useState<Attachment | null>(null);
  const [attachmentError, setAttachmentError] = useState<string | null>(null);
  const fileInputRef = useRef<HTMLInputElement>(null);

  const form = useForm<FormData>({
    resolver: zodResolver(schema),
    defaultValues: { message: '' },
  });

  const watchedMessage = form.watch('message');

  const handleAttachClick = (): void => {
    setAttachmentError(null);
    fileInputRef.current?.click();
  };

  const handleFileChange = (e: ChangeEvent<HTMLInputElement>): void => {
    const file = e.target.files?.[0];
    // Always reset the value so selecting the same file twice still fires change.
    e.target.value = '';
    if (!file) return;
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
    try {
      await sendMessage(conversationId, userMessageId, composed);
    } catch (err) {
      useChatStore.getState().setError(err instanceof Error ? err.message : 'Failed to send message');
    }
  };

  const handleKeyDown = (e: KeyboardEvent<HTMLTextAreaElement>): void => {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      void form.handleSubmit(onSubmit)();
    }
  };

  return (
    <form
      onSubmit={(e) => { void form.handleSubmit(onSubmit)(e); }}
      className="p-3 border-t flex flex-col gap-2 shrink-0"
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
      <Textarea
        {...form.register('message')}
        disabled={isStreaming || disabled}
        onKeyDown={handleKeyDown}
        placeholder="Type a message... (Enter to send, Shift+Enter for newline)"
        rows={3}
      />
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
