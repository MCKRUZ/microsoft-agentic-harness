import { useEffect, useState } from 'react';
import { useAppStore } from '@/stores/appStore';
import { useTelemetryStore } from '@/stores/telemetryStore';
import { useChatStore } from '@/stores/chatStore';
import { useHubActionsStore } from '@/stores/hubActionsStore';
import { ConversationSidebar } from '@/features/conversations/ConversationSidebar';
import { AgentsList } from '@/features/agents/AgentsList';
import { TracesPanel } from '@/features/telemetry/TracesPanel';
import { ToolsBrowser } from '@/features/mcp/ToolsBrowser';
import { ResourcesList } from '@/features/mcp/ResourcesList';
import { PromptsList } from '@/features/mcp/PromptsList';

const TAB_TITLES: Record<string, string> = {
  chats: 'Conversations',
  agents: 'Agents',
  'my-traces': 'My Traces',
  'all-traces': 'All Traces',
  tools: 'MCP Tools',
  resources: 'MCP Resources',
  prompts: 'MCP Prompts',
};

/**
 * Single sidebar column that renders the panel for the currently active tab.
 * Mirrors chatbot-ui's Sidebar + SidebarContent; replaces the old 3-column
 * SplitPanel (Conversations | Chat | Right).
 */
export function Sidebar() {
  const sidebarTab = useAppStore((s) => s.sidebarTab);

  return (
    <aside
      aria-label={TAB_TITLES[sidebarTab] ?? 'Sidebar'}
      className="flex flex-col h-full w-[300px] min-w-[300px] border-r shrink-0 overflow-hidden"
    >
      <div className="flex items-center h-[50px] min-h-[50px] px-3 border-b font-semibold text-sm">
        {TAB_TITLES[sidebarTab]}
      </div>
      <div className="flex-1 overflow-y-auto min-h-0">
        {sidebarTab === 'chats' && <ConversationSidebar />}
        {sidebarTab === 'agents' && <AgentsList />}
        {sidebarTab === 'my-traces' && <MyTracesTab />}
        {sidebarTab === 'all-traces' && <AllTracesTab />}
        {sidebarTab === 'tools' && <ToolsBrowser />}
        {sidebarTab === 'resources' && <ResourcesList />}
        {sidebarTab === 'prompts' && <PromptsList />}
      </div>
    </aside>
  );
}

function MyTracesTab() {
  const activeConversationId = useChatStore((s) => s.conversationId);
  const conversationSpans = useTelemetryStore((s) => s.conversationSpans);
  const clearConversation = useTelemetryStore((s) => s.clearConversation);
  const spans = activeConversationId ? (conversationSpans[activeConversationId] ?? []) : [];
  return (
    <TracesPanel
      spans={spans}
      onClear={activeConversationId ? () => { clearConversation(activeConversationId); } : undefined}
    />
  );
}

function AllTracesTab() {
  const globalSpans = useTelemetryStore((s) => s.globalSpans);
  const clearAll = useTelemetryStore((s) => s.clearAll);
  const joinGlobalTraces = useHubActionsStore((s) => s.joinGlobalTraces);
  const leaveGlobalTraces = useHubActionsStore((s) => s.leaveGlobalTraces);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!joinGlobalTraces) return;
    setError(null);
    let cancelled = false;
    void joinGlobalTraces().catch((err: unknown) => {
      if (cancelled) return;
      const raw = err instanceof Error ? err.message : String(err);
      setError(
        raw.includes('AgentHub.Traces.ReadAll')
          ? 'Requires the AgentHub.Traces.ReadAll role.'
          : raw,
      );
    });
    return () => {
      cancelled = true;
      if (leaveGlobalTraces) void leaveGlobalTraces().catch(() => undefined);
    };
  }, [joinGlobalTraces, leaveGlobalTraces]);

  return (
    <>
      {error && (
        <div className="px-3 py-2 bg-destructive/10 text-destructive text-sm border-b">
          {error}
        </div>
      )}
      <TracesPanel spans={globalSpans} onClear={clearAll} />
    </>
  );
}
