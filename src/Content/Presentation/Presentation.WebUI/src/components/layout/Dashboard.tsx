import { useEffect } from 'react';
import { ChevronsLeft, ChevronsRight } from 'lucide-react';
import { ChatPanel } from '@/features/chat/ChatPanel';
import { Header } from './Header';
import { Sidebar } from './Sidebar';
import { SidebarSwitcher } from './SidebarSwitcher';
import { useAppStore } from '@/stores/appStore';

/**
 * Top-level shell: small header row, then icon rail + sidebar column + full chat.
 * Replaces the old AppShell (Header + 3-column SplitPanel).
 *
 * Hotkeys:
 *   s — toggle the sidebar panel (icon rail stays visible)
 */
export function Dashboard() {
  const showSidebar = useAppStore((s) => s.showSidebar);
  const toggleSidebar = useAppStore((s) => s.toggleSidebar);

  useEffect(() => {
    const onKeyDown = (e: KeyboardEvent): void => {
      // Ignore when typing in an input/textarea/contenteditable.
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
    </div>
  );
}
