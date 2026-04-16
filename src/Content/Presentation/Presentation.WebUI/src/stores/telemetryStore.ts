import { create } from 'zustand';
import type { SpanData } from '@/types/signalr';

// Full implementation in section 11
interface TelemetryStore {
  addConversationSpan: (conversationId: string, span: SpanData) => void;
  addGlobalSpan: (span: SpanData) => void;
  clearAll: () => void;
}

export const useTelemetryStore = create<TelemetryStore>()(() => ({
  addConversationSpan: () => {},
  addGlobalSpan: () => {},
  clearAll: () => {},
}));
