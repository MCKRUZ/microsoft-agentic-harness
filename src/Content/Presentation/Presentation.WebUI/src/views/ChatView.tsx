import { ChevronsLeft, ChevronsRight } from 'lucide-react';
import { ChatPanel } from '@/features/chat/ChatPanel';
import { ConversationSidebar } from '@/features/conversations/ConversationSidebar';
import { useAppStore } from '@/stores/appStore';
import { cn } from '@/lib/utils';

export function ChatView() {
  const showSidebar = useAppStore((s) => s.showSidebar);
  const toggleSidebar = useAppStore((s) => s.toggleSidebar);

  return (
    <div className="relative flex flex-1 min-w-0 h-full">
      {/* Conversation sidebar with smooth transition */}
      <aside
        className={cn(
          'flex flex-col h-full border-r border-border/50 shrink-0 overflow-hidden bg-card/30 transition-all duration-200',
          showSidebar ? 'w-[280px] min-w-[280px]' : 'w-0 min-w-0',
        )}
      >
        {showSidebar && <ConversationSidebar />}
      </aside>

      {/* Main chat area */}
      <main role="main" aria-label="Chat" className="relative flex-1 min-w-0 bg-background">
        <ChatPanel />

        {/* Sidebar toggle */}
        <button
          type="button"
          onClick={toggleSidebar}
          aria-label={showSidebar ? 'Hide conversations (s)' : 'Show conversations (s)'}
          title={showSidebar ? 'Hide conversations (s)' : 'Show conversations (s)'}
          className={cn(
            'absolute left-2 top-1/2 z-10 -translate-y-1/2',
            'rounded-full p-1.5 border border-border/50',
            'bg-background/90 backdrop-blur-sm shadow-sm',
            'text-muted-foreground hover:text-foreground hover:bg-accent',
            'transition-all duration-150',
          )}
        >
          {showSidebar ? <ChevronsLeft size={16} /> : <ChevronsRight size={16} />}
        </button>
      </main>
    </div>
  );
}
