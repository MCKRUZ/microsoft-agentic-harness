import { useAgentsQuery } from './useAgentsQuery';
import { useAppStore } from '@/stores/appStore';

/**
 * Sidebar list of agents with radio-select behaviour. Replaces the dropdown
 * previously in the Header. Starting a turn with an agent selected continues to
 * drive conversation creation; switching clears the active conversation id.
 */
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
    return <div className="p-3 text-sm text-muted-foreground">Loading agents…</div>;
  }
  if (error) {
    return <div className="p-3 text-sm text-destructive">Failed to load agents.</div>;
  }
  if (!agents || agents.length === 0) {
    return <div className="p-3 text-sm text-muted-foreground">No agents configured.</div>;
  }

  return (
    <ul role="listbox" aria-label="Agents" className="flex flex-col gap-1 p-2">
      {agents.map((agent) => {
        const active = agent.id === selectedAgent;
        return (
          <li key={agent.id}>
            <button
              type="button"
              role="option"
              aria-selected={active}
              onClick={() => { handleSelect(agent.id); }}
              className={`w-full text-left rounded px-3 py-2 text-sm transition-colors ${
                active
                  ? 'bg-accent text-foreground'
                  : 'hover:bg-accent/60 text-muted-foreground hover:text-foreground'
              }`}
            >
              <div className="font-medium text-foreground">{agent.name}</div>
              {agent.description && (
                <div className="text-xs text-muted-foreground mt-0.5 line-clamp-2">
                  {agent.description}
                </div>
              )}
            </button>
          </li>
        );
      })}
    </ul>
  );
}
