import { Moon, Sun, Monitor, Command } from 'lucide-react';
import { useMsal } from '@azure/msal-react';
import { useTheme } from '@/hooks/useTheme';
import type { ThemePreference } from '@/components/theme/ThemeProvider';
import { IS_AUTH_DISABLED } from '@/lib/devAuth';
import { useAgentHub, type ConnectionState } from '@/hooks/useAgentHub';
import { cn } from '@/lib/utils';

const THEME_CYCLE: Record<ThemePreference, ThemePreference> = {
  light: 'dark',
  dark: 'system',
  system: 'light',
};

const CONNECTION_META: Record<ConnectionState, { label: string; dotClass: string }> = {
  connected: { label: 'Connected', dotClass: 'bg-emerald-500' },
  connecting: { label: 'Connecting', dotClass: 'bg-amber-500 animate-pulse' },
  reconnecting: { label: 'Reconnecting', dotClass: 'bg-amber-500 animate-pulse' },
  disconnected: { label: 'Disconnected', dotClass: 'bg-red-500' },
};

function ConnectionBadge({ state }: { state: ConnectionState }) {
  const meta = CONNECTION_META[state];
  return (
    <div className="flex items-center gap-1.5 px-2 py-1 rounded-md bg-muted/50 text-xs text-muted-foreground">
      <span className={cn('size-1.5 rounded-full', meta.dotClass)} />
      <span>{meta.label}</span>
    </div>
  );
}

function AuthControls() {
  const { instance, accounts } = useMsal();
  const userName = accounts[0]?.name ?? '';
  const initials = userName
    .split(' ')
    .map((n) => n[0])
    .join('')
    .slice(0, 2)
    .toUpperCase();

  return (
    <div className="flex items-center gap-2">
      {initials && (
        <div className="flex items-center justify-center size-7 rounded-full bg-primary text-primary-foreground text-xs font-medium">
          {initials}
        </div>
      )}
      <button
        onClick={() => { void instance.logoutRedirect(); }}
        className="text-xs px-2.5 py-1 rounded-md border border-border/50 text-muted-foreground hover:bg-accent hover:text-foreground transition-colors"
      >
        Sign out
      </button>
    </div>
  );
}

export function Header() {
  const { preference, setTheme } = useTheme();
  const { connectionState } = useAgentHub();

  const cycleTheme = (): void => { setTheme(THEME_CYCLE[preference]); };
  const themeIcon = preference === 'light'
    ? <Sun size={14} />
    : preference === 'dark'
      ? <Moon size={14} />
      : <Monitor size={14} />;
  const themeLabel = `Theme: ${preference} (click to change)`;

  return (
    <header className="flex items-center justify-between px-4 h-12 min-h-12 border-b border-border/50 shrink-0 bg-background/80 backdrop-blur-sm">
      <div className="flex items-center gap-3">
        <span className="font-semibold text-sm tracking-tight">AgentHub</span>
        <span className="text-muted-foreground/40 text-xs hidden sm:inline">|</span>
        <kbd className="hidden sm:inline-flex items-center gap-0.5 text-[10px] text-muted-foreground/60 font-mono">
          <Command size={10} />K
        </kbd>
      </div>
      <div className="flex items-center gap-2">
        <ConnectionBadge state={connectionState} />
        <button
          onClick={cycleTheme}
          aria-label={themeLabel}
          title={themeLabel}
          className="flex items-center justify-center size-7 rounded-md text-muted-foreground hover:bg-accent hover:text-foreground transition-colors"
        >
          {themeIcon}
        </button>
        {!IS_AUTH_DISABLED && <AuthControls />}
      </div>
    </header>
  );
}
