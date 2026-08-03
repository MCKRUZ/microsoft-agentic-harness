import { create } from 'zustand';
import type { ConversationSettingsInput } from '@/hooks/useAgentHub';

const EMPTY: ConversationSettingsInput = {
  deploymentName: null,
  temperature: null,
  systemPromptOverride: null,
};

interface ConversationSettingsState {
  byConversationId: Record<string, ConversationSettingsInput>;
  getSettings: (conversationId: string | null) => ConversationSettingsInput;
  setSettings: (conversationId: string, settings: ConversationSettingsInput) => void;
  clear: (conversationId: string) => void;
}

export const useConversationSettingsStore = create<ConversationSettingsState>((set, get) => ({
  byConversationId: {},
  getSettings: (conversationId) => {
    if (!conversationId) return EMPTY;
    return get().byConversationId[conversationId] ?? EMPTY;
  },
  setSettings: (conversationId, settings) => {
    set((state) => ({
      byConversationId: { ...state.byConversationId, [conversationId]: settings },
    }));
  },
  clear: (conversationId) => {
    set((state) => {
      // Copy-then-delete rather than a rest-destructure with a discarded binding: the
      // `{ [k]: _removed, ...rest }` form only compiles by naming a variable it never uses.
      // `rest` is a fresh object either way, so the store's state is still replaced, never
      // mutated in place.
      const rest = { ...state.byConversationId };
      delete rest[conversationId];
      return { byConversationId: rest };
    });
  },
}));
