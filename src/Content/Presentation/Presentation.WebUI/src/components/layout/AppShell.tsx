import { Header } from './Header';
import { SplitPanel } from './SplitPanel';
import { Drawer } from '@/components/ui/Drawer';
import { ChatPanel } from '@/features/chat/ChatPanel';
import { RightPanel } from '@/features/telemetry/RightPanel';
import { ConversationSidebar } from '@/features/conversations/ConversationSidebar';
import { useAppStore } from '@/stores/appStore';

export function AppShell() {
  const isSidebarOpen = useAppStore((s) => s.isSidebarOpen);
  const setSidebarOpen = useAppStore((s) => s.setSidebarOpen);

  return (
    <div className="flex flex-col h-screen overflow-hidden">
      <Header />
      <div className="flex-1 overflow-hidden min-h-0">
        <SplitPanel left={<ChatPanel />} right={<RightPanel />} />
      </div>
      <Drawer
        open={isSidebarOpen}
        onOpenChange={setSidebarOpen}
        title="Conversations"
        side="left"
      >
        <ConversationSidebar onSelect={() => { setSidebarOpen(false); }} />
      </Drawer>
    </div>
  );
}
