import { Moon, Sun } from 'lucide-react';
import { useMsal } from '@azure/msal-react';
import { useTheme } from '@/hooks/useTheme';

export function Header() {
  const { theme, toggleTheme } = useTheme();
  const { instance, accounts } = useMsal();
  const userName = accounts[0]?.name ?? '';

  return (
    <header className="flex items-center justify-between px-4 h-16 border-b shrink-0">
      <div className="flex items-center gap-4">
        <span className="font-semibold text-lg">AgentHub</span>
        <select disabled aria-label="Select agent" className="text-sm opacity-50 cursor-not-allowed">
          <option>No agent selected</option>
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
