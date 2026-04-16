import { type KeyboardEvent } from 'react';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { useChatStore } from './useChatStore';
import { Button } from '@/components/ui/button';
import { Textarea } from '@/components/ui/textarea';

const schema = z.object({
  message: z.string().min(1, 'Message is required').max(4000, 'Message too long'),
});

type FormData = z.infer<typeof schema>;

interface ChatInputProps {
  conversationId: string;
  sendMessage: (conversationId: string, message: string) => Promise<void>;
}

export function ChatInput({ conversationId, sendMessage }: ChatInputProps) {
  const isStreaming = useChatStore((s) => s.isStreaming);
  const addMessage = useChatStore((s) => s.addMessage);

  const form = useForm<FormData>({
    resolver: zodResolver(schema),
    defaultValues: { message: '' },
  });

  const watchedMessage = form.watch('message');

  const onSubmit = async (data: FormData): Promise<void> => {
    addMessage({
      id: crypto.randomUUID(),
      role: 'user',
      content: data.message,
      timestamp: new Date(),
    });
    form.reset();
    try {
      await sendMessage(conversationId, data.message);
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
      <Textarea
        {...form.register('message')}
        disabled={isStreaming}
        onKeyDown={handleKeyDown}
        placeholder="Type a message... (Enter to send, Shift+Enter for newline)"
        rows={3}
      />
      {form.formState.errors.message && (
        <p className="text-sm text-destructive">{form.formState.errors.message.message}</p>
      )}
      <div className="flex justify-between items-center">
        <span className="text-xs text-muted-foreground">{watchedMessage.length}/4000</span>
        <Button type="submit" disabled={isStreaming}>
          Send
        </Button>
      </div>
    </form>
  );
}
