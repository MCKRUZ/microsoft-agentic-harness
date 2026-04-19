import { Moon, Sun, Monitor } from 'lucide-react';
import { useMsal } from '@azure/msal-react';
import { useTheme } from '@/hooks/useTheme';
import type { ThemePreference } from '@/components/theme/ThemeProvider';
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

  const cycleTheme = (): void => { setTheme(THEME_CYCLE[preference]); };
  const themeIcon = preference === 'light'
    ? <Sun size={16} />
    : preference === 'dark'
      ? <Moon size={16} />
      : <Monitor size={16} />;
  const themeLabel = `Theme: ${preference} (click to change)`;

  return (
    <header className="flex items-center justify-between px-4 h-[50px] min-h-[50px] border-b shrink-0 bg-background">
      <div className="flex items-center gap-3">
        <span className="font-semibold text-base">AgentHub</span>
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
