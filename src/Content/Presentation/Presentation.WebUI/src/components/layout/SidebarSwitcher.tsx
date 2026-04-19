import { MessageSquare, Bot, Activity, Globe, Wrench, Database, FileText } from 'lucide-react';
import { useAppStore, type SidebarTab } from '@/stores/appStore';

const ICON_SIZE = 20;

interface TabDef {
  value: SidebarTab;
  label: string;
  icon: React.ReactNode;
}

const TABS: readonly TabDef[] = [
  { value: 'chats',       label: 'Conversations', icon: <MessageSquare size={ICON_SIZE} /> },
  { value: 'agents',      label: 'Agents',        icon: <Bot size={ICON_SIZE} /> },
  { value: 'my-traces',   label: 'My Traces',     icon: <Activity size={ICON_SIZE} /> },
  { value: 'all-traces',  label: 'All Traces',    icon: <Globe size={ICON_SIZE} /> },
  { value: 'tools',       label: 'MCP Tools',     icon: <Wrench size={ICON_SIZE} /> },
  { value: 'resources',   label: 'MCP Resources', icon: <Database size={ICON_SIZE} /> },
  { value: 'prompts',     label: 'MCP Prompts',   icon: <FileText size={ICON_SIZE} /> },
];

/**
 * Narrow icon rail on the far left that switches which content type the
 * sidebar shows. Mirrors chatbot-ui's SidebarSwitcher.
 */
export function SidebarSwitcher() {
  const sidebarTab = useAppStore((s) => s.sidebarTab);
  const setSidebarTab = useAppStore((s) => s.setSidebarTab);
  const showSidebar = useAppStore((s) => s.showSidebar);
  const setShowSidebar = useAppStore((s) => s.setShowSidebar);

  const handleClick = (tab: SidebarTab): void => {
    if (tab === sidebarTab && showSidebar) {
      setShowSidebar(false);
      return;
    }
    setSidebarTab(tab);
    if (!showSidebar) setShowSidebar(true);
  };

  return (
    <nav
      aria-label="Sidebar sections"
      className="flex flex-col items-center gap-1 border-r-2 py-2 w-[50px] shrink-0"
    >
      {TABS.map((tab) => {
        const active = tab.value === sidebarTab && showSidebar;
        return (
          <button
            key={tab.value}
            type="button"
            onClick={() => { handleClick(tab.value); }}
            aria-label={tab.label}
            aria-pressed={active}
            title={tab.label}
            className={`flex items-center justify-center rounded size-[36px] transition-colors ${
              active
                ? 'bg-accent text-foreground'
                : 'text-muted-foreground hover:bg-accent hover:text-foreground'
            }`}
          >
            {tab.icon}
          </button>
        );
      })}
    </nav>
  );
}
