import { useMemo, useState } from 'react';
import { MessageCircle, Plus, Trash2, Search } from 'lucide-react';
import { useConversationsQuery } from './useConversationsQuery';
import { useDeleteConversation } from './useDeleteConversation';
import { useAppStore } from '@/stores/appStore';
import { useChatStore } from '@/features/chat/useChatStore';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { ScrollArea } from '@/components/ui/scroll-area';
import { Badge } from '@/components/ui/badge';
import { cn } from '@/lib/utils';

function formatRelative(iso: string): string {
  const ts = new Date(iso).getTime();
  const diffMs = Date.now() - ts;
  const mins = Math.floor(diffMs / 60_000);
  if (mins < 1) return 'just now';
  if (mins < 60) return `${mins}m ago`;
  const hours = Math.floor(mins / 60);
  if (hours < 24) return `${hours}h ago`;
  const days = Math.floor(hours / 24);
  if (days < 7) return `${days}d ago`;
  return new Date(iso).toLocaleDateString();
}

interface ConversationSidebarProps {
  onSelect?: () => void;
}

export function ConversationSidebar({ onSelect }: ConversationSidebarProps) {
  const { data: conversations, isLoading, error } = useConversationsQuery();
  const deleteMutation = useDeleteConversation();
  const activeId = useAppStore((s) => s.activeConversationId);
  const setActive = useAppStore((s) => s.setActiveConversationId);
  const selectedAgent = useAppStore((s) => s.selectedAgent);
  const setSelectedAgent = useAppStore((s) => s.setSelectedAgent);
  const clearMessages = useChatStore((s) => s.clearMessages);
  const [search, setSearch] = useState('');

  const filtered = useMemo(() => {
    if (!conversations) return [];
    const q = search.trim().toLowerCase();
    if (!q) return conversations;
    return conversations.filter((c) => {
      const title = (c.title ?? c.id).toLowerCase();
      return title.includes(q) || c.agentName.toLowerCase().includes(q);
    });
  }, [conversations, search]);

  const handleNewChat = (): void => {
    if (!selectedAgent) return;
    clearMessages();
    setActive(crypto.randomUUID());
    onSelect?.();
  };

  const handleSelect = (id: string): void => {
    if (id === activeId) {
      onSelect?.();
      return;
    }
    const target = conversations?.find((c) => c.id === id);
    clearMessages();
    if (target && target.agentName !== selectedAgent) {
      setSelectedAgent(target.agentName);
    }
    setActive(id);
    onSelect?.();
  };

  const handleDelete = (e: React.MouseEvent, id: string): void => {
    e.stopPropagation();
    deleteMutation.mutate(id);
    if (id === activeId) {
      clearMessages();
      const remaining = conversations?.filter((c) => c.id !== id) ?? [];
      if (remaining.length > 0) {
        const next = remaining[0];
        if (next.agentName !== selectedAgent) setSelectedAgent(next.agentName);
        setActive(next.id);
      } else {
        setActive(crypto.randomUUID());
      }
    }
  };

  return (
    <div className="flex flex-col h-full bg-muted/20">
      <div className="p-3 shrink-0 space-y-2">
        <Button
          variant="outline"
          size="sm"
          onClick={handleNewChat}
          className="w-full justify-start gap-2 h-8 text-xs font-medium border-dashed"
        >
          <Plus size={14} />
          New chat
        </Button>
        <div className="relative">
          <Search
            size={14}
            className="absolute left-2.5 top-1/2 -translate-y-1/2 text-muted-foreground pointer-events-none"
          />
          <Input
            type="text"
            value={search}
            onChange={(e) => { setSearch(e.target.value); }}
            placeholder="Search conversations..."
            aria-label="Search conversations"
            className="h-8 pl-8 text-xs bg-background/50"
          />
        </div>
      </div>
      <ScrollArea className="flex-1 min-h-0">
        {isLoading && (
          <div className="px-3 py-6 text-center">
            <p className="text-xs text-muted-foreground">Loading...</p>
          </div>
        )}
        {error && (
          <div className="px-3 py-6 text-center">
            <p className="text-xs text-destructive">Failed to load conversations</p>
          </div>
        )}
        {!isLoading && !error && filtered.length === 0 && (
          <div className="px-3 py-6 text-center">
            <p className="text-xs text-muted-foreground">
              {search ? 'No matches' : 'No conversations yet'}
            </p>
          </div>
        )}
        <ul className="flex flex-col px-1.5 pb-2">
          {filtered.map((c) => {
            const isActive = c.id === activeId;
            const displayTitle = c.title ?? `Chat ${c.id.slice(0, 8)}`;
            return (
              <li key={c.id}>
                <div
                  role="button"
                  tabIndex={0}
                  onClick={() => { handleSelect(c.id); }}
                  onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); handleSelect(c.id); } }}
                  className={cn(
                    'group w-full text-left px-2.5 py-2 flex items-start gap-2 rounded-md cursor-pointer transition-colors',
                    isActive
                      ? 'bg-accent shadow-sm'
                      : 'hover:bg-accent/50',
                  )}
                  aria-current={isActive ? 'true' : undefined}
                >
                  <MessageCircle size={14} className={cn(
                    'mt-0.5 shrink-0',
                    isActive ? 'text-foreground' : 'text-muted-foreground',
                  )} />
                  <div className="flex-1 min-w-0">
                    <div className={cn(
                      'text-xs font-medium truncate',
                      isActive ? 'text-foreground' : 'text-foreground/80',
                    )}>
                      {displayTitle}
                    </div>
                    <div className="flex items-center gap-1.5 mt-0.5">
                      <Badge variant="secondary" className="h-4 text-[10px] px-1.5">
                        {c.agentName}
                      </Badge>
                      <span className="text-[10px] text-muted-foreground">
                        {formatRelative(c.updatedAt)}
                      </span>
                    </div>
                  </div>
                  <button
                    type="button"
                    aria-label={`Delete conversation ${displayTitle}`}
                    onClick={(e) => { handleDelete(e, c.id); }}
                    className="opacity-0 group-hover:opacity-100 p-1 rounded hover:bg-destructive/20 text-muted-foreground hover:text-destructive shrink-0 transition-opacity"
                  >
                    <Trash2 size={12} />
                  </button>
                </div>
              </li>
            );
          })}
        </ul>
      </ScrollArea>
    </div>
  );
}
