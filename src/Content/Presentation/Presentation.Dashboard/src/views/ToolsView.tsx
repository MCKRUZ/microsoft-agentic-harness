import { PanelView } from '@/components/layout/PanelView';
import { ToolsBrowser } from '@/features/mcp/ToolsBrowser';

export function ToolsView() {
  return (
    <PanelView label="MCP Tools">
      <ToolsBrowser />
    </PanelView>
  );
}
