import { create } from 'zustand';
import type { AgentWidget } from './widgets/types';

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
  /**
   * An inline generative-UI widget the agent rendered mid-run (e.g. an image). When present on an
   * assistant message with empty `content`, the transcript shows only the widget. Rendered via the
   * widget registry.
   */
  widget?: AgentWidget;
}

interface ChatState {
  conversationId: string | null;
  messages: ChatMessage[];
  isStreaming: boolean;
  streamingContent: string;
  error: string | null;
  /**
   * The edited user message awaiting re-insertion after a server-driven history truncation.
   * EditAndResubmit truncates the old user message (and everything after) via HistoryTruncated,
   * which the server sends only once the edit is admitted; this holds the replacement so
   * {@link truncateFrom} can re-insert it in the same step. A refused or declined edit sends no
   * truncation, so the turn's Error or TurnComplete clears it and the original stays (#962).
   */
  pendingEditMessage: ChatMessage | null;
  setConversationId: (id: string) => void;
  addMessage: (message: ChatMessage) => void;
  setMessages: (messages: ChatMessage[]) => void;
  /**
   * Applies a retry's or edit's truncation: drops the message with id `fromMessageId` and everything after it, then
   * re-inserts a staged edit. The cut is anchored on the id, not on `keepCount`, because this list may not match the
   * conversation position for position: it keeps only the newest 200 messages (#961). `null` means the conversation
   * removed nothing. `keepCount` is used only when the named message is not in this list.
   */
  truncateFrom: (fromMessageId: string | null, keepCount: number) => void;
  /**
   * Drops the message with this id, if shown. Used when the server says it removed a refused message from the
   * conversation, so the transcript stops showing text the conversation no longer holds (#1036).
   */
  removeMessage: (id: string) => void;
  setPendingEditMessage: (message: ChatMessage | null) => void;
  startStreaming: () => void;
  appendToken: (token: string) => void;
  finalizeStream: (fullResponse: string, assistantMessageId?: string) => void;
  /**
   * Clears the streaming indicator and any partial buffer WITHOUT touching the transcript. Used when a
   * run is aborted (e.g. switching conversations mid-stream) so the newly opened conversation doesn't
   * inherit a phantom typing indicator or the previous run's leftover tokens.
   */
  resetStreaming: () => void;
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
  truncateFrom: (fromMessageId, keepCount) => set((state) => {
    const at = fromMessageId === null ? state.messages.length : state.messages.findIndex((m) => m.id === fromMessageId);
    const kept = state.messages.slice(0, at >= 0 ? at : Math.max(0, keepCount));
    // An EditAndResubmit truncates the old user message; re-insert the edited replacement
    // so it stays visible in the transcript without waiting for a reload.
    const messages = state.pendingEditMessage ? [...kept, state.pendingEditMessage] : kept;
    return { messages, pendingEditMessage: null };
  }),
  removeMessage: (id) => set((state) => ({ messages: state.messages.filter((m) => m.id !== id) })),
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
      // An admitted edit consumed this on HistoryTruncated. If that notice never arrived (its send can fail without
      // failing the turn), a staged edit left here would be inserted by the next truncation (#962).
      pendingEditMessage: null,
    };
  }),
  resetStreaming: () => set({ isStreaming: false, streamingContent: '' }),
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
