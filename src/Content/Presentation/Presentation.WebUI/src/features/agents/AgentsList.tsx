import { Bot, Check } from 'lucide-react';
import { useAgentsQuery } from './useAgentsQuery';
import { useAppStore } from '@/stores/appStore';
import { cn } from '@/lib/utils';

export function AgentsList() {
  const { data: agents, isLoading, error } = useAgentsQuery();
  const selectedAgent = useAppStore((s) => s.selectedAgent);
  const setSelectedAgent = useAppStore((s) => s.setSelectedAgent);
  const setActiveConversationId = useAppStore((s) => s.setActiveConversationId);

  const handleSelect = (id: string): void => {
    if (id === selectedAgent) return;
    setSelectedAgent(id);
    setActiveConversationId(null);
  };

  if (isLoading) {
    return (
      <div className="flex flex-col gap-3">
        {[1, 2, 3].map((i) => (
          <div key={i} className="h-20 rounded-lg bg-muted/50 animate-pulse" />
        ))}
      </div>
    );
  }
  if (error) {
    return <div className="text-sm text-destructive">Failed to load agents.</div>;
  }
  if (!agents || agents.length === 0) {
    return <div className="text-sm text-muted-foreground">No agents configured.</div>;
  }

  return (
    <ul role="listbox" aria-label="Agents" className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
      {agents.map((agent) => {
        const active = agent.id === selectedAgent;
        return (
          <li key={agent.id}>
            <button
              type="button"
              role="option"
              aria-selected={active}
              onClick={() => { handleSelect(agent.id); }}
              className={cn(
                'relative w-full text-left rounded-lg border p-4 transition-all duration-150 group',
                active
                  ? 'border-primary/50 bg-accent shadow-sm ring-1 ring-primary/20'
                  : 'border-border/50 bg-card hover:border-border hover:shadow-sm',
              )}
            >
              <div className="flex items-start gap-3">
                <div className={cn(
                  'flex items-center justify-center size-9 rounded-lg shrink-0',
                  active ? 'bg-primary/10 text-primary' : 'bg-muted text-muted-foreground',
                )}>
                  <Bot size={18} />
                </div>
                <div className="flex-1 min-w-0">
                  <div className="flex items-center gap-2">
                    <span className="text-sm font-medium text-foreground truncate">
                      {agent.name}
                    </span>
                    {active && (
                      <span className="flex items-center justify-center size-4 rounded-full bg-primary">
                        <Check size={10} className="text-primary-foreground" />
                      </span>
                    )}
                  </div>
                  {agent.description && (
                    <p className="text-xs text-muted-foreground mt-1 line-clamp-2 leading-relaxed">
                      {agent.description}
                    </p>
                  )}
                </div>
              </div>
              <div className="absolute top-3 right-3">
                <span className={cn(
                  'size-2 rounded-full block',
                  active ? 'bg-emerald-500' : 'bg-muted-foreground/30',
                )} />
              </div>
            </button>
          </li>
        );
      })}
    </ul>
  );
}
