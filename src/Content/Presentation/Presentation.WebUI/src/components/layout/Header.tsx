import { Moon, Sun, Monitor, Menu } from 'lucide-react';
import { useMsal } from '@azure/msal-react';
import { useTheme } from '@/hooks/useTheme';
import type { ThemePreference } from '@/components/theme/ThemeProvider';
import { useAgentsQuery } from '@/features/agents/useAgentsQuery';
import { useAppStore } from '@/stores/appStore';
import { IS_AUTH_DISABLED } from '@/lib/devAuth';

const THEME_CYCLE: Record<ThemePreference, ThemePreference> = {
  light: 'dark',
  dark: 'system',
  system: 'light',
};

function AuthControls() {
  const { instance, accounts } = useMsal();
  const userName = accounts[0]?.name ?? '';
  return (
    <>
      {userName && <span className="text-sm text-muted-foreground">{userName}</span>}
      <button
        onClick={() => { void instance.logoutRedirect(); }}
        className="text-sm px-3 py-1 rounded border hover:bg-accent"
      >
        Sign out
      </button>
    </>
  );
}

export function Header() {
  const { preference, setTheme } = useTheme();
  const { data: agents } = useAgentsQuery();
  const selectedAgent = useAppStore((s) => s.selectedAgent);
  const setSelectedAgent = useAppStore((s) => s.setSelectedAgent);
  const setActiveConversationId = useAppStore((s) => s.setActiveConversationId);
  const setSidebarOpen = useAppStore((s) => s.setSidebarOpen);

  const handleAgentChange = (next: string): void => {
    if (!next || next === selectedAgent) return;
    setSelectedAgent(next);
    setActiveConversationId(null);
  };

  const cycleTheme = (): void => { setTheme(THEME_CYCLE[preference]); };
  const themeIcon = preference === 'light'
    ? <Sun size={16} />
    : preference === 'dark'
      ? <Moon size={16} />
      : <Monitor size={16} />;
  const themeLabel = `Theme: ${preference} (click to change)`;

  return (
    <header className="flex items-center justify-between px-4 h-16 border-b shrink-0">
      <div className="flex items-center gap-4">
        <button
          type="button"
          onClick={() => { setSidebarOpen(true); }}
          aria-label="Open conversations"
          title="Conversations"
          className="p-2 rounded hover:bg-accent"
        >
          <Menu size={16} />
        </button>
        <span className="font-semibold text-lg">AgentHub</span>
        <select
          value={selectedAgent ?? ''}
          onChange={(e) => { handleAgentChange(e.target.value); }}
          aria-label="Select agent"
          className="text-sm border rounded px-2 py-1 bg-background"
        >
          <option value="" disabled>No agent selected</option>
          {agents?.map((agent) => (
            <option key={agent.id} value={agent.id}>{agent.name}</option>
          ))}
        </select>
      </div>
      <div className="flex items-center gap-2">
        <button
          onClick={cycleTheme}
          aria-label={themeLabel}
          title={themeLabel}
          className="p-2 rounded hover:bg-accent"
        >
          {themeIcon}
        </button>
        {!IS_AUTH_DISABLED && <AuthControls />}
      </div>
    </header>
  );
}
