import { create } from 'zustand';

interface AppState {
  selectedAgent: string | null;
  activeConversationId: string | null;
  showSidebar: boolean;
  setSelectedAgent: (name: string) => void;
  setActiveConversationId: (id: string | null) => void;
  toggleSidebar: () => void;
}

export const useAppStore = create<AppState>()((set) => ({
  selectedAgent: null,
  activeConversationId: null,
  showSidebar: true,
  setSelectedAgent: (name) => set({ selectedAgent: name }),
  setActiveConversationId: (id) => set({ activeConversationId: id }),
  toggleSidebar: () => set((s) => ({ showSidebar: !s.showSidebar })),
}));
