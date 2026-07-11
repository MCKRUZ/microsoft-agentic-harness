import { create } from 'zustand';
import { persist, createJSONStorage } from 'zustand/middleware';

interface AppState {
  selectedAgent: string | null;
  activeConversationId: string | null;
  showSidebar: boolean;
  setSelectedAgent: (name: string) => void;
  setActiveConversationId: (id: string | null) => void;
  toggleSidebar: () => void;
}

/**
 * App-level UI state. `selectedAgent` is persisted so a page refresh restores the agent the user was
 * working with — without it, refreshing directly on `/chat/:id` fell back to the "select an agent"
 * placeholder even though the conversation id was in the URL. `activeConversationId` is deliberately
 * NOT persisted: it is owned by the URL route param (`/chat/:conversationId`) and mirrored in on load,
 * so persisting it too would let a stale stored id fight the URL. `showSidebar` is a per-session
 * preference and is left unpersisted.
 */
export const useAppStore = create<AppState>()(
  persist(
    (set) => ({
      selectedAgent: null,
      activeConversationId: null,
      showSidebar: true,
      setSelectedAgent: (name) => set({ selectedAgent: name }),
      setActiveConversationId: (id) => set({ activeConversationId: id }),
      toggleSidebar: () => set((s) => ({ showSidebar: !s.showSidebar })),
    }),
    {
      name: 'agenthub-app',
      storage: createJSONStorage(() => localStorage),
      partialize: (s) => ({ selectedAgent: s.selectedAgent }),
    },
  ),
);
