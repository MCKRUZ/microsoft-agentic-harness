import { useEffect, useMemo, useState } from 'react';
import { ChevronsLeft, ChevronsRight } from 'lucide-react';
import { ChatPanel } from '@/features/chat/ChatPanel';
import { Header } from './Header';
import { Sidebar } from './Sidebar';
import { SidebarSwitcher } from './SidebarSwitcher';
import { useAppStore, type SidebarTab } from '@/stores/appStore';
import { CommandPalette, type CommandItem } from '@/features/commands/CommandPalette';
import { useAgentsQuery } from '@/features/agents/useAgentsQuery';
import { useTheme } from '@/hooks/useTheme';

/**
 * Top-level shell: small header row, then icon rail + sidebar column + full chat.
 * Replaces the old AppShell (Header + 3-column SplitPanel).
 *
 * Hotkeys:
 *   s          — toggle the sidebar panel (icon rail stays visible)
 *   Cmd/Ctrl+K — open the command palette
 */
export function Dashboard() {
  const showSidebar = useAppStore((s) => s.showSidebar);
  const toggleSidebar = useAppStore((s) => s.toggleSidebar);
  const setActiveConversationId = useAppStore((s) => s.setActiveConversationId);
  const setSelectedAgent = useAppStore((s) => s.setSelectedAgent);
  const setSidebarTab = useAppStore((s) => s.setSidebarTab);
  const setShowSidebar = useAppStore((s) => s.setShowSidebar);
  const selectedAgent = useAppStore((s) => s.selectedAgent);
  const [paletteOpen, setPaletteOpen] = useState(false);
  const agentsQuery = useAgentsQuery();
  const { resolvedTheme, toggleTheme } = useTheme();

  useEffect(() => {
    const onKeyDown = (e: KeyboardEvent): void => {
      // Cmd/Ctrl+K opens the palette from anywhere, including inputs.
      if (e.key.toLowerCase() === 'k' && (e.metaKey || e.ctrlKey) && !e.altKey && !e.shiftKey) {
        e.preventDefault();
        setPaletteOpen((o) => !o);
        return;
      }
      // Ignore other hotkeys when typing in an input/textarea/contenteditable.
      const t = e.target as HTMLElement | null;
      if (t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.isContentEditable)) return;
      if (e.key === 's' && !e.metaKey && !e.ctrlKey && !e.altKey) {
        e.preventDefault();
        toggleSidebar();
      }
    };
    window.addEventListener('keydown', onKeyDown);
    return () => { window.removeEventListener('keydown', onKeyDown); };
  }, [toggleSidebar]);

  const commands = useMemo<CommandItem[]>(() => {
    const items: CommandItem[] = [
      {
        id: 'new-conversation',
        label: 'New conversation',
        hint: 'Reset the current chat',
        group: 'Chat',
        keywords: ['reset', 'clear', 'start'],
        run: () => { setActiveConversationId(crypto.randomUUID()); },
      },
      {
        id: 'toggle-sidebar',
        label: showSidebar ? 'Hide sidebar' : 'Show sidebar',
        hint: 's',
        group: 'View',
        keywords: ['panel', 'nav'],
        run: () => { toggleSidebar(); },
      },
      {
        id: 'toggle-theme',
        label: resolvedTheme === 'dark' ? 'Switch to light theme' : 'Switch to dark theme',
        group: 'View',
        keywords: ['dark', 'light', 'appearance'],
        run: () => { toggleTheme(); },
      },
    ];
    const tabs: { tab: SidebarTab; label: string }[] = [
      { tab: 'chats', label: 'Chats' },
      { tab: 'agents', label: 'Agents' },
      { tab: 'my-traces', label: 'My traces' },
      { tab: 'all-traces', label: 'All traces' },
      { tab: 'tools', label: 'Tools' },
      { tab: 'resources', label: 'Resources' },
      { tab: 'prompts', label: 'Prompts' },
    ];
    for (const { tab, label } of tabs) {
      items.push({
        id: `goto-${tab}`,
        label: `Go to ${label}`,
        group: 'Navigate',
        keywords: [tab],
        run: () => {
          setSidebarTab(tab);
          setShowSidebar(true);
        },
      });
    }
    for (const agent of agentsQuery.data ?? []) {
      const current = selectedAgent === agent.name;
      items.push({
        id: `agent-${agent.id}`,
        label: `Switch to agent: ${agent.name}`,
        hint: current ? 'current' : agent.description,
        group: 'Agents',
        keywords: ['switch', 'agent', agent.name],
        run: () => {
          setSelectedAgent(agent.name);
          setActiveConversationId(null);
        },
      });
    }
    return items;
  }, [
    showSidebar,
    resolvedTheme,
    agentsQuery.data,
    selectedAgent,
    setActiveConversationId,
    toggleSidebar,
    toggleTheme,
    setSidebarTab,
    setShowSidebar,
    setSelectedAgent,
  ]);

  return (
    <div className="flex flex-col h-screen overflow-hidden">
      <Header />
      <div className="flex flex-1 min-h-0 overflow-hidden">
        <SidebarSwitcher />
        {showSidebar && <Sidebar />}
        <main role="main" aria-label="Chat" className="relative flex-1 min-w-0 bg-muted/40">
          <ChatPanel />
          <button
            type="button"
            onClick={toggleSidebar}
            aria-label={showSidebar ? 'Hide sidebar (s)' : 'Show sidebar (s)'}
            title={showSidebar ? 'Hide sidebar (s)' : 'Show sidebar (s)'}
            className="absolute left-1 top-1/2 z-10 -translate-y-1/2 rounded p-1 text-muted-foreground hover:bg-accent hover:text-foreground"
          >
            {showSidebar ? <ChevronsLeft size={18} /> : <ChevronsRight size={18} />}
          </button>
        </main>
      </div>
      <CommandPalette
        open={paletteOpen}
        onClose={() => { setPaletteOpen(false); }}
        commands={commands}
      />
    </div>
  );
}
