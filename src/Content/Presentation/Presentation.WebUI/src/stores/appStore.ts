import { create } from 'zustand';

interface AppState {
  selectedAgent: string | null;
  activeConversationId: string | null;
  isSidebarOpen: boolean;
  setSelectedAgent: (name: string) => void;
  setActiveConversationId: (id: string | null) => void;
  setSidebarOpen: (open: boolean) => void;
}

export const useAppStore = create<AppState>()((set) => ({
  selectedAgent: null,
  activeConversationId: null,
  isSidebarOpen: false,
  setSelectedAgent: (name) => set({ selectedAgent: name }),
  setActiveConversationId: (id) => set({ activeConversationId: id }),
  setSidebarOpen: (open) => set({ isSidebarOpen: open }),
}));
