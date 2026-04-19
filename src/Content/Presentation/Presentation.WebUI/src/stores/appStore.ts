import { create } from 'zustand';

/**
 * Tabs shown in the sidebar icon rail. Mirrors the chatbot-ui content-type enum
 * but scoped to our harness surfaces (agents + MCP + traces instead of
 * presets/assistants/files/collections/tools/models).
 */
export type SidebarTab =
  | 'chats'
  | 'agents'
  | 'my-traces'
  | 'all-traces'
  | 'tools'
  | 'resources'
  | 'prompts';

interface AppState {
  selectedAgent: string | null;
  activeConversationId: string | null;
  /** Whether the sidebar panel (to the right of the icon rail) is visible. */
  showSidebar: boolean;
  /** Which tab the sidebar is currently showing. */
  sidebarTab: SidebarTab;
  setSelectedAgent: (name: string) => void;
  setActiveConversationId: (id: string | null) => void;
  setShowSidebar: (open: boolean) => void;
  toggleSidebar: () => void;
  setSidebarTab: (tab: SidebarTab) => void;
}

export const useAppStore = create<AppState>()((set) => ({
  selectedAgent: null,
  activeConversationId: null,
  showSidebar: true,
  sidebarTab: 'chats',
  setSelectedAgent: (name) => set({ selectedAgent: name }),
  setActiveConversationId: (id) => set({ activeConversationId: id }),
  setShowSidebar: (open) => set({ showSidebar: open }),
  toggleSidebar: () => set((s) => ({ showSidebar: !s.showSidebar })),
  setSidebarTab: (tab) => set({ sidebarTab: tab }),
}));
