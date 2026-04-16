import { create } from 'zustand';

interface AppState {
  selectedAgent: string | null;
  setSelectedAgent: (name: string) => void;
}

export const useAppStore = create<AppState>()((set) => ({
  selectedAgent: null,
  setSelectedAgent: (name) => set({ selectedAgent: name }),
}));
