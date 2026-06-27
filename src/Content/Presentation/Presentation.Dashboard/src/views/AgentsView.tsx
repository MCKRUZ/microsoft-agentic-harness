import { PanelView } from '@/components/layout/PanelView';
import { AgentsList } from '@/features/agents/AgentsList';

export function AgentsView() {
  return (
    <PanelView label="Agents">
      <AgentsList />
    </PanelView>
  );
}
