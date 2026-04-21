import { useEffect, useMemo, useState } from 'react';
import { Outlet, useNavigate, useLocation } from 'react-router-dom';
import { Header } from './Header';
import { SidebarSwitcher } from './SidebarSwitcher';
import { useAppStore } from '@/stores/appStore';
import { CommandPalette, type CommandItem } from '@/features/commands/CommandPalette';
import { useAgentsQuery } from '@/features/agents/useAgentsQuery';
import { useTheme } from '@/hooks/useTheme';
import { NAV_ITEMS } from '@/lib/navigation';

export function DashboardLayout() {
  const toggleSidebar = useAppStore((s) => s.toggleSidebar);
  const showSidebar = useAppStore((s) => s.showSidebar);
  const setActiveConversationId = useAppStore((s) => s.setActiveConversationId);
  const setSelectedAgent = useAppStore((s) => s.setSelectedAgent);
  const selectedAgent = useAppStore((s) => s.selectedAgent);
  const [paletteOpen, setPaletteOpen] = useState(false);
  const agentsQuery = useAgentsQuery();
  const { resolvedTheme, toggleTheme } = useTheme();
  const navigate = useNavigate();
  const { pathname } = useLocation();

  useEffect(() => {
    if (!selectedAgent && agentsQuery.data?.length) {
      setSelectedAgent(agentsQuery.data[0].id);
    }
  }, [selectedAgent, agentsQuery.data, setSelectedAgent]);

  useEffect(() => {
    const onKeyDown = (e: KeyboardEvent): void => {
      if (e.key.toLowerCase() === 'k' && (e.metaKey || e.ctrlKey) && !e.altKey && !e.shiftKey) {
        e.preventDefault();
        setPaletteOpen((o) => !o);
        return;
      }
      const t = e.target as HTMLElement | null;
      if (t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.isContentEditable)) return;
      if (e.key === 's' && !e.metaKey && !e.ctrlKey && !e.altKey && pathname === '/chat') {
        e.preventDefault();
        toggleSidebar();
      }
    };
    window.addEventListener('keydown', onKeyDown);
    return () => { window.removeEventListener('keydown', onKeyDown); };
  }, [toggleSidebar, pathname]);

  const commands = useMemo<CommandItem[]>(() => {
    const items: CommandItem[] = [
      {
        id: 'new-conversation',
        label: 'New conversation',
        hint: 'Reset the current chat',
        group: 'Chat',
        keywords: ['reset', 'clear', 'start'],
        run: () => {
          setActiveConversationId(crypto.randomUUID());
          navigate('/chat');
        },
      },
      {
        id: 'toggle-sidebar',
        label: showSidebar ? 'Hide conversations' : 'Show conversations',
        hint: 's (chat view only)',
        group: 'View',
        keywords: ['panel', 'nav', 'sidebar'],
        run: () => {
          if (pathname !== '/chat') navigate('/chat');
          toggleSidebar();
        },
      },
      {
        id: 'toggle-theme',
        label: resolvedTheme === 'dark' ? 'Switch to light theme' : 'Switch to dark theme',
        group: 'View',
        keywords: ['dark', 'light', 'appearance'],
        run: () => { toggleTheme(); },
      },
    ];
    for (const { path, label, keywords } of NAV_ITEMS) {
      items.push({
        id: `goto-${path}`,
        label: `Go to ${label}`,
        group: 'Navigate',
        keywords,
        run: () => { navigate(path); },
      });
    }
    for (const agent of agentsQuery.data ?? []) {
      const current = selectedAgent === agent.id;
      items.push({
        id: `agent-${agent.id}`,
        label: `Switch to agent: ${agent.name}`,
        hint: current ? 'current' : agent.description,
        group: 'Agents',
        keywords: ['switch', 'agent', agent.name],
        run: () => {
          setSelectedAgent(agent.id);
          setActiveConversationId(null);
          navigate('/chat');
        },
      });
    }
    return items;
  }, [
    showSidebar,
    resolvedTheme,
    pathname,
    agentsQuery.data,
    selectedAgent,
    setActiveConversationId,
    toggleSidebar,
    toggleTheme,
    setSelectedAgent,
    navigate,
  ]);

  return (
    <div className="flex flex-col h-screen overflow-hidden bg-background">
      <Header />
      <div className="flex flex-1 min-h-0 overflow-hidden">
        <SidebarSwitcher />
        <Outlet />
      </div>
      <CommandPalette
        open={paletteOpen}
        onClose={() => { setPaletteOpen(false); }}
        commands={commands}
      />
    </div>
  );
}
