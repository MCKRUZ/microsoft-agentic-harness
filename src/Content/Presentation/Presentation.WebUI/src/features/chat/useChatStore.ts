import { create } from 'zustand';

export interface ToolCallSummary {
  toolName: string;
  input: Record<string, unknown>;
  output: unknown;
}

export interface ChatMessage {
  id: string;
  role: 'user' | 'assistant';
  content: string;
  timestamp: Date;
  toolCalls?: ToolCallSummary[];
}

interface ChatState {
  conversationId: string | null;
  messages: ChatMessage[];
  isStreaming: boolean;
  streamingContent: string;
  error: string | null;
  /**
   * The edited user message awaiting re-insertion after a server-driven history truncation.
   * EditAndResubmit truncates the old user message (and everything after) via HistoryTruncated;
   * this holds the replacement so {@link truncateAfter} can re-insert it in the same step,
   * keeping the local transcript consistent without waiting for a reload.
   */
  pendingEditMessage: ChatMessage | null;
  setConversationId: (id: string) => void;
  addMessage: (message: ChatMessage) => void;
  setMessages: (messages: ChatMessage[]) => void;
  truncateAfter: (keepCount: number) => void;
  setPendingEditMessage: (message: ChatMessage | null) => void;
  startStreaming: () => void;
  appendToken: (token: string) => void;
  finalizeStream: (fullResponse: string, assistantMessageId?: string) => void;
  clearMessages: () => void;
  setError: (message: string | null) => void;
}

export const useChatStore = create<ChatState>()((set) => ({
  conversationId: null,
  messages: [],
  isStreaming: false,
  streamingContent: '',
  error: null,
  pendingEditMessage: null,
  setConversationId: (id) => set({ conversationId: id }),
  addMessage: (message) => set((state) => {
    const messages = [...state.messages, message];
    return { messages: messages.length > 200 ? messages.slice(-200) : messages };
  }),
  setMessages: (messages) => set({ messages }),
  truncateAfter: (keepCount) => set((state) => {
    const kept = state.messages.slice(0, Math.max(0, keepCount));
    // An EditAndResubmit truncates the old user message; re-insert the edited replacement
    // so it stays visible in the transcript without waiting for a reload.
    const messages = state.pendingEditMessage ? [...kept, state.pendingEditMessage] : kept;
    return { messages, pendingEditMessage: null };
  }),
  setPendingEditMessage: (message) => set({ pendingEditMessage: message }),
  startStreaming: () => set({ isStreaming: true, streamingContent: '' }),
  appendToken: (token) => set((state) => state.isStreaming
    ? { streamingContent: state.streamingContent + token }
    : { isStreaming: true, streamingContent: state.streamingContent + token }
  ),
  finalizeStream: (fullResponse, assistantMessageId) => set((state) => {
    const message: ChatMessage = {
      id: assistantMessageId ?? crypto.randomUUID(),
      role: 'assistant',
      content: fullResponse,
      timestamp: new Date(),
    };
    const messages = [...state.messages, message];
    return {
      isStreaming: false,
      streamingContent: '',
      messages: messages.length > 200 ? messages.slice(-200) : messages,
    };
  }),
  clearMessages: () => set({
    messages: [],
    isStreaming: false,
    streamingContent: '',
    error: null,
    pendingEditMessage: null,
  }),
  setError: (message) => set((state) => ({
    error: message,
    isStreaming: message != null ? false : state.isStreaming,
    streamingContent: message != null ? '' : state.streamingContent,
  })),
}));
