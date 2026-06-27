import { PanelView } from '@/components/layout/PanelView';
import { PromptsList } from '@/features/mcp/PromptsList';

export function PromptsView() {
  return (
    <PanelView label="MCP Prompts">
      <PromptsList />
    </PanelView>
  );
}
