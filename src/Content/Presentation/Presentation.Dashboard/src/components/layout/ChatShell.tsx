import { useEffect } from 'react';
import { Outlet } from 'react-router-dom';
import { Sidebar } from './Sidebar';
import { ThemeToggle } from '@/components/theme/ThemeToggle';
import { AgentHubProvider, useAgentHub, type ConnectionState } from '@/hooks/useAgentHub';
import { useAgentsQuery } from '@/features/agents/useAgentsQuery';
import { useAppStore } from '@/stores/appStore';
import { cn } from '@/lib/utils';

const CONNECTION_META: Record<ConnectionState, { label: string; dotClass: string }> = {
  connected: { label: 'Connected', dotClass: 'bg-emerald-500' },
  connecting: { label: 'Connecting', dotClass: 'bg-amber-500 animate-pulse' },
  reconnecting: { label: 'Reconnecting', dotClass: 'bg-amber-500 animate-pulse' },
  disconnected: { label: 'Disconnected', dotClass: 'bg-red-500' },
};

/** Live SignalR connection indicator for the chat surface. */
function ConnectionBadge() {
  const { connectionState } = useAgentHub();
  const meta = CONNECTION_META[connectionState];
  return (
    <div className="flex items-center gap-1.5 px-2 py-1 rounded-md bg-muted/50 text-xs text-muted-foreground">
      <span className={cn('size-1.5 rounded-full', meta.dotClass)} />
      <span>{meta.label}</span>
    </div>
  );
}

/**
 * Chat-surface top bar. Distinct from the observability {@link Topbar} (which
 * carries the time-range picker, telemetry-LIVE pip, and refresh) because none
 * of that applies to a conversation view.
 */
function ChatHeader() {
  return (
    <header className="flex items-center justify-between px-5 h-12 min-h-12 border-b border-border bg-background shrink-0">
      <span className="text-sm font-semibold tracking-tight text-foreground">Agent Chat</span>
      <div className="flex items-center gap-2">
        <ConnectionBadge />
        <ThemeToggle />
      </div>
    </header>
  );
}

/**
 * Default the active agent to the first available one when nothing is selected
 * yet, so the chat opens ready-to-use instead of on the "pick an agent" empty
 * state. Mirrors the bootstrap the standalone WebUI shell performed.
 */
function useDefaultAgentSelection(): void {
  const selectedAgent = useAppStore((s) => s.selectedAgent);
  const setSelectedAgent = useAppStore((s) => s.setSelectedAgent);
  const agentsQuery = useAgentsQuery();

  useEffect(() => {
    const first = agentsQuery.data?.[0];
    if (!selectedAgent && first) {
      setSelectedAgent(first.id);
    }
  }, [selectedAgent, agentsQuery.data, setSelectedAgent]);
}

/**
 * Layout for the agent-interaction routes (`/agent/*`). Shares the Dashboard
 * {@link Sidebar} for navigation, but provides a chat-specific header and a
 * full-bleed body (the chat/MCP views supply their own <main>). The SignalR
 * conversation hub is mounted here — and only here — so observability-only
 * pages never open an idle agent connection.
 */
export default function ChatShell() {
  useDefaultAgentSelection();

  return (
    <div className="flex h-screen overflow-hidden">
      <Sidebar />
      <AgentHubProvider>
        <div className="flex-1 flex flex-col overflow-hidden">
          <ChatHeader />
          <div className="flex flex-1 min-h-0 overflow-hidden">
            <Outlet />
          </div>
        </div>
      </AgentHubProvider>
    </div>
  );
}
