import { Moon, Sun } from 'lucide-react';
import { useMsal } from '@azure/msal-react';
import { useTheme } from '@/hooks/useTheme';
import { useAgentsQuery } from '@/features/agents/useAgentsQuery';
import { useAppStore } from '@/stores/appStore';

export function Header() {
  const { theme, toggleTheme } = useTheme();
  const { instance, accounts } = useMsal();
  const userName = accounts[0]?.name ?? '';
  const { data: agents } = useAgentsQuery();
  const selectedAgent = useAppStore((s) => s.selectedAgent);
  const setSelectedAgent = useAppStore((s) => s.setSelectedAgent);

  return (
    <header className="flex items-center justify-between px-4 h-16 border-b shrink-0">
      <div className="flex items-center gap-4">
        <span className="font-semibold text-lg">AgentHub</span>
        <select
          value={selectedAgent ?? ''}
          onChange={(e) => { if (e.target.value) setSelectedAgent(e.target.value); }}
          aria-label="Select agent"
          className="text-sm border rounded px-2 py-1 bg-background"
        >
          <option value="" disabled>No agent selected</option>
          {agents?.map((agent) => (
            <option key={agent.name} value={agent.name}>{agent.name}</option>
          ))}
        </select>
      </div>
      <div className="flex items-center gap-2">
        <button
          onClick={toggleTheme}
          aria-label="Toggle theme"
          className="p-2 rounded hover:bg-accent"
        >
          {theme === 'dark' ? <Sun size={16} /> : <Moon size={16} />}
        </button>
        {userName && (
          <span className="text-sm text-muted-foreground">{userName}</span>
        )}
        <button
          onClick={() => { void instance.logoutRedirect(); }}
          className="text-sm px-3 py-1 rounded border hover:bg-accent"
        >
          Sign out
        </button>
      </div>
    </header>
  );
}
