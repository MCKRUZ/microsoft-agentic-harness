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
  addMessage: (message) => set((state) => ({ messages: [...state.messages, message] })),
  setMessages: (messages) => set({ messages }),
  appendToken: (token) => set((state) => ({
    isStreaming: true,
    streamingContent: state.streamingContent + token,
  })),
  finalizeStream: (fullResponse) => set((state) => ({
    isStreaming: false,
    streamingContent: '',
    messages: [
      ...state.messages,
      {
        id: crypto.randomUUID(),
        role: 'assistant' as const,
        content: fullResponse,
        timestamp: new Date(),
      },
    ],
  })),
  clearMessages: () => set((state) => ({
    conversationId: state.conversationId,
    messages: [],
    isStreaming: false,
    streamingContent: '',
    error: null,
  })),
  setError: (message) => set({ error: message }),
}));
