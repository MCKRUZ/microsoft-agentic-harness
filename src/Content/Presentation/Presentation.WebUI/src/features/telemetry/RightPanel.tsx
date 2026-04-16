import { useState } from 'react';
import { useTelemetryStore } from '@/stores/telemetryStore';
import { useChatStore } from '@/stores/chatStore';
import { TracesPanel } from './TracesPanel';
import { ToolsBrowser } from '@/features/mcp/ToolsBrowser';
import { ResourcesList } from '@/features/mcp/ResourcesList';
import { PromptsList } from '@/features/mcp/PromptsList';

const TABS = [
  { value: 'my-traces', label: 'My Traces' },
  { value: 'all-traces', label: 'All Traces' },
  { value: 'tools', label: 'Tools' },
  { value: 'resources', label: 'Resources' },
  { value: 'prompts', label: 'Prompts' },
] as const;

type TabValue = (typeof TABS)[number]['value'];

export function RightPanel() {
  const [activeTab, setActiveTab] = useState<TabValue>('my-traces');

  const activeConversationId = useChatStore((s) => s.conversationId);
  const conversationSpans = useTelemetryStore((s) => s.conversationSpans);
  const globalSpans = useTelemetryStore((s) => s.globalSpans);
  const clearConversation = useTelemetryStore((s) => s.clearConversation);
  const clearAll = useTelemetryStore((s) => s.clearAll);

  const mySpans = (activeConversationId ? (conversationSpans[activeConversationId] ?? []) : []);

  return (
    <div className="flex flex-col h-full">
      <div className="sticky top-0 z-10 flex border-b shrink-0 bg-background overflow-x-auto">
        {TABS.map((tab) => (
          <button
            key={tab.value}
            type="button"
            onClick={() => { setActiveTab(tab.value); }}
            className={`px-3 py-2 text-sm whitespace-nowrap border-b-2 -mb-px transition-colors ${
              activeTab === tab.value
                ? 'border-primary text-foreground font-medium'
                : 'border-transparent text-muted-foreground hover:text-foreground'
            }`}
          >
            {tab.label}
          </button>
        ))}
      </div>

      <div className="flex-1 overflow-y-auto min-h-0">
        {activeTab === 'my-traces' && (
          <TracesPanel
            spans={mySpans}
            onClear={activeConversationId ? () => { clearConversation(activeConversationId); } : undefined}
          />
        )}
        {activeTab === 'all-traces' && (
          <TracesPanel spans={globalSpans} onClear={clearAll} />
        )}
        {activeTab === 'tools' && <ToolsBrowser />}
        {activeTab === 'resources' && <ResourcesList />}
        {activeTab === 'prompts' && <PromptsList />}
      </div>
    </div>
  );
}
