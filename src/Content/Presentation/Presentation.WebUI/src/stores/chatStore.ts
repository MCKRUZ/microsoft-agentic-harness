import { create } from 'zustand';

// Full implementation in section 10
export interface ChatMessage {
  id: string;
  role: 'user' | 'assistant' | 'system';
  content: string;
  timestamp: string;
}

interface ChatStore {
  messages: ChatMessage[];
  streamingToken: string;
  error: string | null;
  appendToken: (token: string) => void;
  finalizeStream: (message: ChatMessage) => void;
  setError: (message: string) => void;
  setMessages: (messages: ChatMessage[]) => void;
}

export const useChatStore = create<ChatStore>()(() => ({
  messages: [],
  streamingToken: '',
  error: null,
  appendToken: () => {},
  finalizeStream: () => {},
  setError: () => {},
  setMessages: () => {},
}));
