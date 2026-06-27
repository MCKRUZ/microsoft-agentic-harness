import { PanelView } from '@/components/layout/PanelView';
import { ResourcesList } from '@/features/mcp/ResourcesList';

export function ResourcesView() {
  return (
    <PanelView label="MCP Resources">
      <ResourcesList />
    </PanelView>
  );
}
