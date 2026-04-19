import { useMemo, useState } from 'react';
import { MessageSquare, Plus, Trash2, Search } from 'lucide-react';
import { useConversationsQuery } from './useConversationsQuery';
import { useDeleteConversation } from './useDeleteConversation';
import { useAppStore } from '@/stores/appStore';
import { useChatStore } from '@/features/chat/useChatStore';

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
    clearMessages();
    setActive(null);
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
      // Switch the agent first; the ChatPanel effect that reacts to agent
      // changes would otherwise clear activeConversationId again.
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
      setActive(null);
    }
  };

  return (
    <div className="flex flex-col h-full">
      <div className="p-3 border-b shrink-0 flex flex-col gap-2">
        <button
          type="button"
          onClick={handleNewChat}
          className="flex items-center gap-2 px-3 py-2 rounded border hover:bg-accent text-sm font-medium"
        >
          <Plus size={14} />
          New chat
        </button>
        <div className="relative">
          <Search
            size={14}
            className="absolute left-2 top-1/2 -translate-y-1/2 text-muted-foreground pointer-events-none"
          />
          <input
            type="text"
            value={search}
            onChange={(e) => { setSearch(e.target.value); }}
            placeholder="Search"
            aria-label="Search conversations"
            className="w-full pl-7 pr-2 py-1.5 text-sm border rounded bg-background focus:outline-none focus:ring-1 focus:ring-ring"
          />
        </div>
      </div>
      <div className="flex-1 overflow-y-auto">
        {isLoading && <p className="p-3 text-sm text-muted-foreground">Loading…</p>}
        {error && <p className="p-3 text-sm text-destructive">Failed to load conversations</p>}
        {!isLoading && !error && filtered.length === 0 && (
          <p className="p-3 text-sm text-muted-foreground">
            {search ? 'No matches' : 'No conversations yet'}
          </p>
        )}
        <ul className="flex flex-col">
          {filtered.map((c) => {
            const isActive = c.id === activeId;
            const displayTitle = c.title ?? `Chat ${c.id.slice(0, 8)}`;
            return (
              <li key={c.id}>
                <button
                  type="button"
                  onClick={() => { handleSelect(c.id); }}
                  className={`group w-full text-left px-3 py-2 flex items-start gap-2 hover:bg-accent ${
                    isActive ? 'bg-accent' : ''
                  }`}
                  aria-current={isActive ? 'true' : undefined}
                >
                  <MessageSquare size={14} className="mt-0.5 shrink-0 text-muted-foreground" />
                  <div className="flex-1 min-w-0">
                    <div className="text-sm truncate">{displayTitle}</div>
                    <div className="text-xs text-muted-foreground truncate">
                      {c.agentName} · {formatRelative(c.updatedAt)}
                    </div>
                  </div>
                  <button
                    type="button"
                    aria-label={`Delete conversation ${displayTitle}`}
                    onClick={(e) => { handleDelete(e, c.id); }}
                    className="opacity-0 group-hover:opacity-100 p-1 rounded hover:bg-destructive/20 text-muted-foreground hover:text-destructive shrink-0"
                  >
                    <Trash2 size={14} />
                  </button>
                </button>
              </li>
            );
          })}
        </ul>
      </div>
    </div>
  );
}
