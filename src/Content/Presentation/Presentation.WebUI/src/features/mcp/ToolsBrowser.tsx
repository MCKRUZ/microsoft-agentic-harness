import { useState } from 'react';
import { useToolsQuery, type McpTool } from './useMcpQuery';
import { ToolInvoker } from './ToolInvoker';

export function ToolsBrowser() {
  const { data: tools, isLoading, isError } = useToolsQuery();
  const [selectedTool, setSelectedTool] = useState<McpTool | null>(null);

  if (isLoading) {
    return <div className="p-4 text-sm text-muted-foreground">Loading tools…</div>;
  }

  if (isError) {
    return <div className="p-4 text-sm text-destructive">Failed to load tools.</div>;
  }

  return (
    <div className="grid grid-cols-[200px_1fr] h-full overflow-hidden">
      <div className="border-r overflow-y-auto">
        {tools?.map((tool) => (
          <button
            key={tool.name}
            type="button"
            onClick={() => { setSelectedTool(tool); }}
            className={`w-full text-left px-3 py-2 text-sm border-b hover:bg-accent truncate ${selectedTool?.name === tool.name ? 'bg-accent font-medium' : ''}`}
          >
            {tool.name}
          </button>
        ))}
      </div>

      <div className="overflow-y-auto p-3">
        {selectedTool ? (
          <>
            <h3 className="font-semibold text-sm mb-1">{selectedTool.name}</h3>
            <p className="text-sm text-muted-foreground mb-2">{selectedTool.description}</p>
            <pre className="text-xs bg-muted p-2 rounded overflow-auto max-h-40 mb-2">
              {JSON.stringify(selectedTool.inputSchema, null, 2)}
            </pre>
            <ToolInvoker tool={selectedTool} />
          </>
        ) : (
          <div className="text-sm text-muted-foreground p-4 text-center">
            Select a tool to view details and invoke it.
          </div>
        )}
      </div>
    </div>
  );
}
