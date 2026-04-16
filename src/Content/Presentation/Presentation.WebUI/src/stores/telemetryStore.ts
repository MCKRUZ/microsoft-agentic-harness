import { create } from 'zustand';
import type { SpanData } from '@/types/signalr';

const MAX_GLOBAL_SPANS = 500;
const MAX_CONVERSATION_SPANS = 200;

interface TelemetryState {
  conversationSpans: Record<string, SpanData[]>;
  globalSpans: SpanData[];
  addSpan: (span: SpanData) => void;
  addConversationSpan: (conversationId: string, span: SpanData) => void;
  addGlobalSpan: (span: SpanData) => void;
  clearConversation: (conversationId: string) => void;
  clearAll: () => void;
}

export const useTelemetryStore = create<TelemetryState>()((set) => ({
  conversationSpans: {},
  globalSpans: [],

  addSpan: (span) =>
    set((state) => {
      const conversationId = span.conversationId ?? '';
      const existing = state.conversationSpans[conversationId] ?? [];
      const updatedConv = [...existing, span];
      const updatedGlobal = [...state.globalSpans, span];
      return {
        conversationSpans: {
          ...state.conversationSpans,
          [conversationId]:
            updatedConv.length > MAX_CONVERSATION_SPANS
              ? updatedConv.slice(-MAX_CONVERSATION_SPANS)
              : updatedConv,
        },
        globalSpans: updatedGlobal.length > MAX_GLOBAL_SPANS
          ? updatedGlobal.slice(-MAX_GLOBAL_SPANS)
          : updatedGlobal,
      };
    }),

  addConversationSpan: (conversationId, span) =>
    set((state) => {
      const existing = state.conversationSpans[conversationId] ?? [];
      const updated = [...existing, span];
      return {
        conversationSpans: {
          ...state.conversationSpans,
          [conversationId]:
            updated.length > MAX_CONVERSATION_SPANS
              ? updated.slice(-MAX_CONVERSATION_SPANS)
              : updated,
        },
      };
    }),

  addGlobalSpan: (span) =>
    set((state) => {
      const updated = [...state.globalSpans, span];
      return {
        globalSpans: updated.length > MAX_GLOBAL_SPANS ? updated.slice(-MAX_GLOBAL_SPANS) : updated,
      };
    }),

  clearConversation: (conversationId) =>
    set((state) => ({
      conversationSpans: Object.fromEntries(
        Object.entries(state.conversationSpans).filter(([k]) => k !== conversationId),
      ),
    })),

  clearAll: () => set({ conversationSpans: {}, globalSpans: [] }),
}));
