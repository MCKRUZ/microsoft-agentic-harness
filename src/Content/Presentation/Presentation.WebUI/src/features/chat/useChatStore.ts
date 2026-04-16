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
  setConversationId: (id: string) => void;
  addMessage: (message: ChatMessage) => void;
  setMessages: (messages: ChatMessage[]) => void;
  appendToken: (token: string) => void;
  finalizeStream: (fullResponse: string) => void;
  clearMessages: () => void;
  setError: (message: string | null) => void;
}

export const useChatStore = create<ChatState>()((set) => ({
  conversationId: null,
  messages: [],
  isStreaming: false,
  streamingContent: '',
  error: null,
  setConversationId: (id) => set({ conversationId: id }),
  addMessage: (message) => set((state) => {
    const messages = [...state.messages, message];
    return { messages: messages.length > 200 ? messages.slice(-200) : messages };
  }),
  setMessages: (messages) => set({ messages }),
  appendToken: (token) => set((state) => state.isStreaming
    ? { streamingContent: state.streamingContent + token }
    : { isStreaming: true, streamingContent: state.streamingContent + token }
  ),
  finalizeStream: (fullResponse) => set((state) => {
    const message: ChatMessage = {
      id: crypto.randomUUID(),
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
  }),
  setError: (message) => set({ error: message }),
}));
