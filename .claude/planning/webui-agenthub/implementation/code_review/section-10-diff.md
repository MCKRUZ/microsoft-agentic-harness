diff --git a/src/Content/Presentation/Presentation.WebUI/src/__tests__/App.test.tsx b/src/Content/Presentation/Presentation.WebUI/src/__tests__/App.test.tsx
index ca1a945..7195d71 100644
--- a/src/Content/Presentation/Presentation.WebUI/src/__tests__/App.test.tsx
+++ b/src/Content/Presentation/Presentation.WebUI/src/__tests__/App.test.tsx
@@ -21,6 +21,17 @@ vi.mock('@azure/msal-react', () => ({
   }),
 }));
 
+vi.mock('@/hooks/useAgentHub', () => ({
+  useAgentHub: () => ({
+    connectionState: 'connected' as const,
+    sendMessage: vi.fn().mockResolvedValue(undefined),
+    startConversation: vi.fn().mockResolvedValue(undefined),
+    invokeToolViaAgent: vi.fn().mockResolvedValue(undefined),
+    joinGlobalTraces: vi.fn().mockResolvedValue(undefined),
+    leaveGlobalTraces: vi.fn().mockResolvedValue(undefined),
+  }),
+}));
+
 vi.mock('@/lib/authConfig', () => ({
   msalConfig: {},
   loginRequest: { scopes: [] },
diff --git a/src/Content/Presentation/Presentation.WebUI/src/components/layout/AppShell.tsx b/src/Content/Presentation/Presentation.WebUI/src/components/layout/AppShell.tsx
index ef1c31d..095db83 100644
--- a/src/Content/Presentation/Presentation.WebUI/src/components/layout/AppShell.tsx
+++ b/src/Content/Presentation/Presentation.WebUI/src/components/layout/AppShell.tsx
@@ -1,12 +1,13 @@
 import { Header } from './Header';
 import { SplitPanel } from './SplitPanel';
+import { ChatPanel } from '@/features/chat/ChatPanel';
 
 export function AppShell() {
   return (
     <div className="flex flex-col h-screen overflow-hidden">
       <Header />
       <div className="flex-1 overflow-hidden min-h-0">
-        <SplitPanel left={<div />} right={<div />} />
+        <SplitPanel left={<ChatPanel />} right={<div />} />
       </div>
     </div>
   );
diff --git a/src/Content/Presentation/Presentation.WebUI/src/components/ui/textarea.tsx b/src/Content/Presentation/Presentation.WebUI/src/components/ui/textarea.tsx
new file mode 100644
index 0000000..b5805d4
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/components/ui/textarea.tsx
@@ -0,0 +1,18 @@
+import type { TextareaHTMLAttributes } from 'react';
+
+type TextareaProps = TextareaHTMLAttributes<HTMLTextAreaElement>;
+
+export function Textarea({ className = '', ...props }: TextareaProps) {
+  return (
+    <textarea
+      className={[
+        'flex min-h-[60px] w-full rounded-md border border-input bg-transparent px-3 py-2 text-sm',
+        'shadow-sm placeholder:text-muted-foreground',
+        'focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring',
+        'disabled:cursor-not-allowed disabled:opacity-50',
+        className,
+      ].join(' ')}
+      {...props}
+    />
+  );
+}
diff --git a/src/Content/Presentation/Presentation.WebUI/src/features/chat/ChatInput.tsx b/src/Content/Presentation/Presentation.WebUI/src/features/chat/ChatInput.tsx
new file mode 100644
index 0000000..0555516
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/features/chat/ChatInput.tsx
@@ -0,0 +1,72 @@
+import { type KeyboardEvent } from 'react';
+import { useForm } from 'react-hook-form';
+import { zodResolver } from '@hookform/resolvers/zod';
+import { z } from 'zod';
+import { useChatStore } from './useChatStore';
+import { Button } from '@/components/ui/button';
+import { Textarea } from '@/components/ui/textarea';
+
+const schema = z.object({
+  message: z.string().min(1, 'Message is required').max(4000, 'Message too long'),
+});
+
+type FormData = z.infer<typeof schema>;
+
+interface ChatInputProps {
+  conversationId: string;
+  sendMessage: (conversationId: string, message: string) => Promise<void>;
+}
+
+export function ChatInput({ conversationId, sendMessage }: ChatInputProps) {
+  const isStreaming = useChatStore((s) => s.isStreaming);
+  const addMessage = useChatStore((s) => s.addMessage);
+
+  const form = useForm<FormData>({
+    resolver: zodResolver(schema),
+    defaultValues: { message: '' },
+  });
+
+  const watchedMessage = form.watch('message');
+
+  const onSubmit = async (data: FormData): Promise<void> => {
+    addMessage({
+      id: crypto.randomUUID(),
+      role: 'user',
+      content: data.message,
+      timestamp: new Date(),
+    });
+    form.reset();
+    await sendMessage(conversationId, data.message);
+  };
+
+  const handleKeyDown = (e: KeyboardEvent<HTMLTextAreaElement>): void => {
+    if (e.key === 'Enter' && !e.shiftKey) {
+      e.preventDefault();
+      void form.handleSubmit(onSubmit)();
+    }
+  };
+
+  return (
+    <form
+      onSubmit={(e) => { void form.handleSubmit(onSubmit)(e); }}
+      className="p-3 border-t flex flex-col gap-2 shrink-0"
+    >
+      <Textarea
+        {...form.register('message')}
+        disabled={isStreaming}
+        onKeyDown={handleKeyDown}
+        placeholder="Type a message... (Enter to send, Shift+Enter for newline)"
+        rows={3}
+      />
+      {form.formState.errors.message && (
+        <p className="text-sm text-destructive">{form.formState.errors.message.message}</p>
+      )}
+      <div className="flex justify-between items-center">
+        <span className="text-xs text-muted-foreground">{watchedMessage.length}/4000</span>
+        <Button type="submit" disabled={isStreaming}>
+          Send
+        </Button>
+      </div>
+    </form>
+  );
+}
diff --git a/src/Content/Presentation/Presentation.WebUI/src/features/chat/ChatPanel.tsx b/src/Content/Presentation/Presentation.WebUI/src/features/chat/ChatPanel.tsx
new file mode 100644
index 0000000..56797fe
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/features/chat/ChatPanel.tsx
@@ -0,0 +1,60 @@
+import { useEffect } from 'react';
+import { useChatStore } from './useChatStore';
+import { useAppStore } from '@/stores/appStore';
+import { useAgentHub } from '@/hooks/useAgentHub';
+import { MessageList } from './MessageList';
+import { TypingIndicator } from './TypingIndicator';
+import { ChatInput } from './ChatInput';
+
+function ConversationHeader() {
+  const conversationId = useChatStore((s) => s.conversationId);
+  const clearMessages = useChatStore((s) => s.clearMessages);
+
+  return (
+    <div className="flex items-center justify-between px-3 py-2 border-b shrink-0">
+      <span className="text-sm text-muted-foreground font-mono truncate max-w-[200px]">
+        {conversationId ? `${conversationId.slice(0, 8)}\u2026` : 'No conversation'}
+      </span>
+      <button
+        type="button"
+        onClick={clearMessages}
+        className="text-xs text-muted-foreground hover:text-foreground shrink-0 ml-2"
+      >
+        Clear
+      </button>
+    </div>
+  );
+}
+
+export function ChatPanel() {
+  const setConversationId = useChatStore((s) => s.setConversationId);
+  const conversationId = useChatStore((s) => s.conversationId);
+  const selectedAgent = useAppStore((s) => s.selectedAgent);
+  const { sendMessage, startConversation } = useAgentHub();
+
+  useEffect(() => {
+    if (!conversationId) {
+      const newId = crypto.randomUUID();
+      setConversationId(newId);
+      if (selectedAgent) {
+        void startConversation(selectedAgent, newId).catch(() => {
+          // Connection may not be ready on first mount; user can retry by sending a message
+        });
+      }
+    }
+  // eslint-disable-next-line react-hooks/exhaustive-deps
+  }, []);
+
+  return (
+    <div className="flex flex-col h-full">
+      <ConversationHeader />
+      <div className="flex-1 overflow-hidden min-h-0">
+        <MessageList />
+      </div>
+      <TypingIndicator />
+      {conversationId && (
+        <ChatInput conversationId={conversationId} sendMessage={sendMessage} />
+      )}
+    </div>
+  );
+}
diff --git a/src/Content/Presentation/Presentation.WebUI/src/features/chat/MessageItem.tsx b/src/Content/Presentation/Presentation.WebUI/src/features/chat/MessageItem.tsx
new file mode 100644
index 0000000..2c36e70
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/features/chat/MessageItem.tsx
@@ -0,0 +1,84 @@
+import { useState } from 'react';
+import type { ReactNode } from 'react';
+import type { ChatMessage, ToolCallSummary } from './useChatStore';
+
+function ToolCallChip({ toolCall }: { toolCall: ToolCallSummary }) {
+  const [expanded, setExpanded] = useState(false);
+  return (
+    <div className="mt-1">
+      <button
+        type="button"
+        onClick={() => { setExpanded(!expanded); }}
+        className="text-xs bg-muted-foreground/20 rounded px-2 py-0.5 hover:bg-muted-foreground/30"
+      >
+        {toolCall.toolName}
+      </button>
+      {expanded && (
+        <pre className="text-xs mt-1 p-2 bg-muted rounded overflow-auto max-h-40">
+          {JSON.stringify({ input: toolCall.input, output: toolCall.output }, null, 2)}
+        </pre>
+      )}
+    </div>
+  );
+}
+
+function parseContent(content: string): ReactNode {
+  const parts: ReactNode[] = [];
+  const codeRegex = /```[\w]*\n([\s\S]*?)```/g;
+  let lastIndex = 0;
+  let match: RegExpExecArray | null;
+
+  while ((match = codeRegex.exec(content)) !== null) {
+    const before = content.slice(lastIndex, match.index);
+    if (before) {
+      parts.push(<p key={`text-${lastIndex}`} className="whitespace-pre-wrap">{before}</p>);
+    }
+    const codeContent = match[1] ?? '';
+    const matchStart = match.index;
+    parts.push(
+      <pre key={`code-${matchStart}`} className="bg-muted rounded p-2 my-1 overflow-auto text-sm">
+        <code>{codeContent}</code>
+      </pre>,
+    );
+    lastIndex = match.index + (match[0]?.length ?? 0);
+  }
+
+  const remaining = content.slice(lastIndex);
+  if (remaining || parts.length === 0) {
+    parts.push(<p key={`text-end-${lastIndex}`} className="whitespace-pre-wrap">{remaining}</p>);
+  }
+
+  return <>{parts}</>;
+}
+
+interface MessageItemProps {
+  message: ChatMessage;
+  isStreaming?: boolean;
+}
+
+export function MessageItem({ message, isStreaming = false }: MessageItemProps) {
+  const isUser = message.role === 'user';
+
+  return (
+    <div className={`flex ${isUser ? 'justify-end' : 'justify-start'} px-3 py-1`}>
+      <div
+        className={
+          isUser
+            ? 'ml-auto bg-primary text-primary-foreground rounded-lg p-3 max-w-[80%]'
+            : 'mr-auto bg-muted rounded-lg p-3 max-w-[80%]'
+        }
+      >
+        {parseContent(message.content)}
+        {isStreaming && (
+          <span
+            className="inline-block w-1.5 h-4 bg-current animate-pulse ml-0.5 align-middle"
+            aria-hidden
+          />
+        )}
+        {(message.toolCalls ?? []).map((tc, i) => (
+          <ToolCallChip key={i} toolCall={tc} />
+        ))}
+      </div>
+    </div>
+  );
+}
diff --git a/src/Content/Presentation/Presentation.WebUI/src/features/chat/MessageList.tsx b/src/Content/Presentation/Presentation.WebUI/src/features/chat/MessageList.tsx
new file mode 100644
index 0000000..0ccd4c1
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/features/chat/MessageList.tsx
@@ -0,0 +1,31 @@
+import { useRef, useEffect } from 'react';
+import { useChatStore } from './useChatStore';
+import { MessageItem } from './MessageItem';
+
+export function MessageList() {
+  const messages = useChatStore((s) => s.messages);
+  const isStreaming = useChatStore((s) => s.isStreaming);
+  const streamingContent = useChatStore((s) => s.streamingContent);
+  const bottomRef = useRef<HTMLDivElement>(null);
+
+  const hasStreamingItem = isStreaming && streamingContent.length > 0;
+
+  useEffect(() => {
+    bottomRef.current?.scrollIntoView({ behavior: 'smooth' });
+  }, [messages.length, hasStreamingItem]);
+
+  return (
+    <div className="flex flex-col gap-1 h-full overflow-y-auto">
+      {messages.map((message) => (
+        <MessageItem key={message.id} message={message} />
+      ))}
+      {hasStreamingItem && (
+        <MessageItem
+          message={{ id: 'streaming', role: 'assistant', content: streamingContent, timestamp: new Date() }}
+          isStreaming
+        />
+      )}
+      <div ref={bottomRef} aria-hidden />
+    </div>
+  );
+}
diff --git a/src/Content/Presentation/Presentation.WebUI/src/features/chat/TypingIndicator.tsx b/src/Content/Presentation/Presentation.WebUI/src/features/chat/TypingIndicator.tsx
new file mode 100644
index 0000000..592a6e7
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/features/chat/TypingIndicator.tsx
@@ -0,0 +1,18 @@
+import { useChatStore } from './useChatStore';
+
+export function TypingIndicator() {
+  const isStreaming = useChatStore((s) => s.isStreaming);
+  if (!isStreaming) return null;
+
+  return (
+    <div className="flex gap-1 px-3 py-2 shrink-0" aria-label="Agent is typing">
+      {[0, 150, 300].map((delay) => (
+        <span
+          key={delay}
+          className="w-2 h-2 rounded-full bg-muted-foreground animate-bounce"
+          style={{ animationDelay: `${delay}ms` }}
+        />
+      ))}
+    </div>
+  );
+}
diff --git a/src/Content/Presentation/Presentation.WebUI/src/features/chat/__tests__/ChatInput.test.tsx b/src/Content/Presentation/Presentation.WebUI/src/features/chat/__tests__/ChatInput.test.tsx
new file mode 100644
index 0000000..fc5f9dd
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/features/chat/__tests__/ChatInput.test.tsx
@@ -0,0 +1,58 @@
+import { render, screen, fireEvent } from '@testing-library/react';
+import userEvent from '@testing-library/user-event';
+import { vi, describe, it, expect, beforeEach } from 'vitest';
+import { useChatStore } from '../useChatStore';
+import { ChatInput } from '../ChatInput';
+
+const mockSendMessage = vi.fn().mockResolvedValue(undefined);
+
+function renderInput() {
+  return render(<ChatInput conversationId="test-conv" sendMessage={mockSendMessage} />);
+}
+
+describe('ChatInput', () => {
+  beforeEach(() => {
+    vi.clearAllMocks();
+    useChatStore.setState({ conversationId: null, messages: [], isStreaming: false, streamingContent: '' });
+  });
+
+  it('submit calls sendMessage with input value', async () => {
+    const user = userEvent.setup();
+    renderInput();
+    await user.type(screen.getByPlaceholderText(/type a message/i), 'hello world');
+    await user.click(screen.getByRole('button', { name: /send/i }));
+    expect(mockSendMessage).toHaveBeenCalledWith('test-conv', 'hello world');
+  });
+
+  it('is disabled while isStreaming is true', () => {
+    useChatStore.setState({ isStreaming: true });
+    renderInput();
+    expect(screen.getByPlaceholderText(/type a message/i)).toBeDisabled();
+    expect(screen.getByRole('button', { name: /send/i })).toBeDisabled();
+  });
+
+  it('clears after submit', async () => {
+    const user = userEvent.setup();
+    renderInput();
+    await user.type(screen.getByPlaceholderText(/type a message/i), 'hello world');
+    await user.click(screen.getByRole('button', { name: /send/i }));
+    expect(screen.getByPlaceholderText(/type a message/i)).toHaveValue('');
+  });
+
+  it('rejects empty string (does not call sendMessage)', async () => {
+    const user = userEvent.setup();
+    renderInput();
+    await user.click(screen.getByRole('button', { name: /send/i }));
+    expect(mockSendMessage).not.toHaveBeenCalled();
+  });
+
+  it('rejects messages over 4000 characters', async () => {
+    renderInput();
+    fireEvent.change(screen.getByPlaceholderText(/type a message/i), {
+      target: { value: 'a'.repeat(4001) },
+    });
+    fireEvent.click(screen.getByRole('button', { name: /send/i }));
+    expect(await screen.findByText(/message too long/i)).toBeInTheDocument();
+    expect(mockSendMessage).not.toHaveBeenCalled();
+  });
+});
diff --git a/src/Content/Presentation/Presentation.WebUI/src/features/chat/__tests__/MessageList.test.tsx b/src/Content/Presentation/Presentation.WebUI/src/features/chat/__tests__/MessageList.test.tsx
new file mode 100644
index 0000000..276e77f
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/features/chat/__tests__/MessageList.test.tsx
@@ -0,0 +1,70 @@
+import { render, screen, act } from '@testing-library/react';
+import { describe, it, expect, beforeEach } from 'vitest';
+import { useChatStore } from '../useChatStore';
+import { MessageList } from '../MessageList';
+import { TypingIndicator } from '../TypingIndicator';
+
+const seedMessages = () => {
+  useChatStore.getState().addMessage({ id: '1', role: 'user', content: 'Hello', timestamp: new Date() });
+  useChatStore.getState().addMessage({ id: '2', role: 'assistant', content: 'Hi there', timestamp: new Date() });
+  useChatStore.getState().addMessage({ id: '3', role: 'user', content: 'How are you?', timestamp: new Date() });
+};
+
+describe('MessageList', () => {
+  beforeEach(() => {
+    useChatStore.setState({
+      conversationId: null,
+      messages: [],
+      isStreaming: false,
+      streamingContent: '',
+      error: null,
+    });
+  });
+
+  it('renders all messages from the store', () => {
+    seedMessages();
+    render(<MessageList />);
+    expect(screen.getByText('Hello')).toBeInTheDocument();
+    expect(screen.getByText('Hi there')).toBeInTheDocument();
+    expect(screen.getByText('How are you?')).toBeInTheDocument();
+  });
+
+  it('renders user message right-aligned (ml-auto)', () => {
+    useChatStore.getState().addMessage({ id: '1', role: 'user', content: 'User msg', timestamp: new Date() });
+    render(<MessageList />);
+    const msgEl = screen.getByText('User msg');
+    expect(msgEl.closest('[class*="ml-auto"]')).toBeInTheDocument();
+  });
+
+  it('renders assistant message left-aligned (mr-auto)', () => {
+    useChatStore.getState().addMessage({ id: '1', role: 'assistant', content: 'Agent msg', timestamp: new Date() });
+    render(<MessageList />);
+    const msgEl = screen.getByText('Agent msg');
+    expect(msgEl.closest('[class*="mr-auto"]')).toBeInTheDocument();
+  });
+
+  it('streaming: streamingContent updates visible text in DOM', () => {
+    useChatStore.setState({ isStreaming: true, streamingContent: 'partial response' });
+    render(<MessageList />);
+    expect(screen.getByText('partial response')).toBeInTheDocument();
+  });
+
+  it('TurnComplete: finalizeStream hides streaming content and shows final message', () => {
+    useChatStore.setState({ isStreaming: true, streamingContent: 'partial' });
+    render(
+      <>
+        <MessageList />
+        <TypingIndicator />
+      </>,
+    );
+    expect(screen.getByText('partial')).toBeInTheDocument();
+    expect(screen.getByLabelText('Agent is typing')).toBeInTheDocument();
+
+    act(() => {
+      useChatStore.getState().finalizeStream('Full response');
+    });
+
+    expect(screen.queryByLabelText('Agent is typing')).not.toBeInTheDocument();
+    expect(screen.getByText('Full response')).toBeInTheDocument();
+  });
+});
diff --git a/src/Content/Presentation/Presentation.WebUI/src/features/chat/__tests__/TypingIndicator.test.tsx b/src/Content/Presentation/Presentation.WebUI/src/features/chat/__tests__/TypingIndicator.test.tsx
new file mode 100644
index 0000000..a5199e4
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/features/chat/__tests__/TypingIndicator.test.tsx
@@ -0,0 +1,21 @@
+import { render, screen } from '@testing-library/react';
+import { describe, it, expect, beforeEach } from 'vitest';
+import { useChatStore } from '../useChatStore';
+import { TypingIndicator } from '../TypingIndicator';
+
+describe('TypingIndicator', () => {
+  beforeEach(() => {
+    useChatStore.setState({ isStreaming: false, messages: [], streamingContent: '', conversationId: null });
+  });
+
+  it('is visible when isStreaming is true', () => {
+    useChatStore.setState({ isStreaming: true });
+    render(<TypingIndicator />);
+    expect(screen.getByLabelText('Agent is typing')).toBeInTheDocument();
+  });
+
+  it('is not rendered when isStreaming is false', () => {
+    render(<TypingIndicator />);
+    expect(screen.queryByLabelText('Agent is typing')).not.toBeInTheDocument();
+  });
+});
diff --git a/src/Content/Presentation/Presentation.WebUI/src/features/chat/__tests__/useChatStore.test.ts b/src/Content/Presentation/Presentation.WebUI/src/features/chat/__tests__/useChatStore.test.ts
new file mode 100644
index 0000000..a904663
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/features/chat/__tests__/useChatStore.test.ts
@@ -0,0 +1,48 @@
+import { describe, it, expect, beforeEach } from 'vitest';
+import { useChatStore } from '../useChatStore';
+
+describe('useChatStore', () => {
+  beforeEach(() => {
+    useChatStore.setState({
+      conversationId: null,
+      messages: [],
+      isStreaming: false,
+      streamingContent: '',
+    });
+  });
+
+  it('appendToken accumulates tokens in streamingContent', () => {
+    const { appendToken } = useChatStore.getState();
+    appendToken('hello ');
+    appendToken('world');
+    const state = useChatStore.getState();
+    expect(state.streamingContent).toBe('hello world');
+    expect(state.isStreaming).toBe(true);
+  });
+
+  it('finalizeStream clears streamingContent and adds message to messages array', () => {
+    const store = useChatStore.getState();
+    store.appendToken('hello ');
+    store.appendToken('world');
+    store.finalizeStream('hello world');
+    const state = useChatStore.getState();
+    expect(state.streamingContent).toBe('');
+    expect(state.isStreaming).toBe(false);
+    expect(state.messages).toHaveLength(1);
+    expect(state.messages[0]?.content).toBe('hello world');
+    expect(state.messages[0]?.role).toBe('assistant');
+  });
+
+  it('clearMessages resets all state', () => {
+    const store = useChatStore.getState();
+    store.addMessage({ id: '1', role: 'user', content: 'hi', timestamp: new Date() });
+    store.appendToken('token');
+    store.setConversationId('conv-1');
+    store.clearMessages();
+    const state = useChatStore.getState();
+    expect(state.messages).toHaveLength(0);
+    expect(state.isStreaming).toBe(false);
+    expect(state.streamingContent).toBe('');
+    expect(state.conversationId).toBe('conv-1');
+  });
+});
diff --git a/src/Content/Presentation/Presentation.WebUI/src/features/chat/useChatStore.ts b/src/Content/Presentation/Presentation.WebUI/src/features/chat/useChatStore.ts
new file mode 100644
index 0000000..0442eca
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/features/chat/useChatStore.ts
@@ -0,0 +1,65 @@
+import { create } from 'zustand';
+
+export interface ToolCallSummary {
+  toolName: string;
+  input: Record<string, unknown>;
+  output: unknown;
+}
+
+export interface ChatMessage {
+  id: string;
+  role: 'user' | 'assistant';
+  content: string;
+  timestamp: Date;
+  toolCalls?: ToolCallSummary[];
+}
+
+interface ChatState {
+  conversationId: string | null;
+  messages: ChatMessage[];
+  isStreaming: boolean;
+  streamingContent: string;
+  error: string | null;
+  setConversationId: (id: string) => void;
+  addMessage: (message: ChatMessage) => void;
+  setMessages: (messages: ChatMessage[]) => void;
+  appendToken: (token: string) => void;
+  finalizeStream: (fullResponse: string) => void;
+  clearMessages: () => void;
+  setError: (message: string | null) => void;
+}
+
+export const useChatStore = create<ChatState>()((set) => ({
+  conversationId: null,
+  messages: [],
+  isStreaming: false,
+  streamingContent: '',
+  error: null,
+  setConversationId: (id) => set({ conversationId: id }),
+  addMessage: (message) => set((state) => ({ messages: [...state.messages, message] })),
+  setMessages: (messages) => set({ messages }),
+  appendToken: (token) => set((state) => ({
+    isStreaming: true,
+    streamingContent: state.streamingContent + token,
+  })),
+  finalizeStream: (fullResponse) => set((state) => ({
+    isStreaming: false,
+    streamingContent: '',
+    messages: [
+      ...state.messages,
+      {
+        id: crypto.randomUUID(),
+        role: 'assistant' as const,
+        content: fullResponse,
+        timestamp: new Date(),
+      },
+    ],
+  })),
+  clearMessages: () => set((state) => ({
+    conversationId: state.conversationId,
+    messages: [],
+    isStreaming: false,
+    streamingContent: '',
+  })),
+  setError: (message) => set({ error: message }),
+}));
diff --git a/src/Content/Presentation/Presentation.WebUI/src/hooks/useAgentHub.ts b/src/Content/Presentation/Presentation.WebUI/src/hooks/useAgentHub.ts
index 4933126..acd80ab 100644
--- a/src/Content/Presentation/Presentation.WebUI/src/hooks/useAgentHub.ts
+++ b/src/Content/Presentation/Presentation.WebUI/src/hooks/useAgentHub.ts
@@ -42,8 +42,8 @@ export function useAgentHub(): UseAgentHubReturn {
       useChatStore.getState().appendToken(token);
     });
 
-    connection.on('TurnComplete', (message: ChatMessage) => {
-      useChatStore.getState().finalizeStream(message);
+    connection.on('TurnComplete', (message: { content: string }) => {
+      useChatStore.getState().finalizeStream(message.content);
     });
 
     connection.on('Error', (message: string) => {
diff --git a/src/Content/Presentation/Presentation.WebUI/src/stores/chatStore.ts b/src/Content/Presentation/Presentation.WebUI/src/stores/chatStore.ts
index 9549663..11cfbda 100644
--- a/src/Content/Presentation/Presentation.WebUI/src/stores/chatStore.ts
+++ b/src/Content/Presentation/Presentation.WebUI/src/stores/chatStore.ts
@@ -1,29 +1 @@
-import { create } from 'zustand';
-
-// Full implementation in section 10
-export interface ChatMessage {
-  id: string;
-  role: 'user' | 'assistant' | 'system';
-  content: string;
-  timestamp: string;
-}
-
-interface ChatStore {
-  messages: ChatMessage[];
-  streamingToken: string;
-  error: string | null;
-  appendToken: (token: string) => void;
-  finalizeStream: (message: ChatMessage) => void;
-  setError: (message: string) => void;
-  setMessages: (messages: ChatMessage[]) => void;
-}
-
-export const useChatStore = create<ChatStore>()(() => ({
-  messages: [],
-  streamingToken: '',
-  error: null,
-  appendToken: () => {},
-  finalizeStream: () => {},
-  setError: () => {},
-  setMessages: () => {},
-}));
+export { useChatStore, type ChatMessage, type ToolCallSummary } from '@/features/chat/useChatStore';
diff --git a/src/Content/Presentation/Presentation.WebUI/src/test/setup.ts b/src/Content/Presentation/Presentation.WebUI/src/test/setup.ts
index 7ed5cad..b0ac35c 100644
--- a/src/Content/Presentation/Presentation.WebUI/src/test/setup.ts
+++ b/src/Content/Presentation/Presentation.WebUI/src/test/setup.ts
@@ -16,4 +16,7 @@ Object.defineProperty(window, 'matchMedia', {
   })),
 });
 
+// scrollIntoView — not implemented in jsdom
+Element.prototype.scrollIntoView = vi.fn();
+
 // MSW server setup added in section 12
