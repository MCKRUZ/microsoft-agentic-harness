import { useState, useMemo } from 'react';
import { Wrench, Search, ChevronRight } from 'lucide-react';
import { useToolsQuery, type McpTool } from './useMcpQuery';
import { ToolInvoker } from './ToolInvoker';
import { Card, CardHeader, CardTitle, CardDescription, CardContent } from '@/components/ui/card';
import { Badge } from '@/components/ui/badge';
import { Input } from '@/components/ui/input';
import { JsonViewer } from '@/components/ui/json-viewer';
import { EmptyState } from '@/components/ui/empty-state';
import { cn } from '@/lib/utils';

function ToolSchemaParams({ schema }: { schema: Record<string, unknown> }) {
  const properties = schema.properties as Record<string, { type?: string; description?: string }> | undefined;
  const required = (schema.required as string[]) ?? [];

  if (!properties || Object.keys(properties).length === 0) {
    return <span className="text-xs text-muted-foreground italic">No parameters</span>;
  }

  return (
    <div className="flex flex-wrap gap-1.5">
      {Object.entries(properties).map(([name, prop]) => (
        <Badge
          key={name}
          variant={required.includes(name) ? 'default' : 'outline'}
          className="text-[10px] font-mono"
        >
          {name}
          {prop.type && <span className="ml-0.5 opacity-70">:{prop.type}</span>}
        </Badge>
      ))}
    </div>
  );
}

function ToolDetailPanel({ tool }: { tool: McpTool }) {
  return (
    <div className="flex flex-col gap-4">
      <div>
        <div className="flex items-center gap-2 mb-1">
          <Wrench size={16} className="text-primary shrink-0" />
          <h3 className="font-semibold text-base">{tool.name}</h3>
        </div>
        {tool.description && (
          <p className="text-sm text-muted-foreground mt-1 leading-relaxed">{tool.description}</p>
        )}
      </div>

      <div>
        <h4 className="text-xs font-medium uppercase tracking-wide text-muted-foreground mb-2">Parameters</h4>
        <ToolSchemaParams schema={tool.inputSchema} />
      </div>

      <div>
        <h4 className="text-xs font-medium uppercase tracking-wide text-muted-foreground mb-2">Input Schema</h4>
        <JsonViewer data={tool.inputSchema} defaultExpanded={true} maxInitialDepth={2} />
      </div>

      <div>
        <h4 className="text-xs font-medium uppercase tracking-wide text-muted-foreground mb-2">Invoke</h4>
        <ToolInvoker tool={tool} />
      </div>
    </div>
  );
}

function ToolListSkeleton() {
  return (
    <div className="flex flex-col gap-2 p-4">
      {Array.from({ length: 5 }).map((_, i) => (
        <div key={i} className="h-16 rounded-lg bg-muted/50 animate-pulse" />
      ))}
    </div>
  );
}

export function ToolsBrowser() {
  const { data: tools, isLoading, isError } = useToolsQuery();
  const [selectedTool, setSelectedTool] = useState<McpTool | null>(null);
  const [search, setSearch] = useState('');

  const filteredTools = useMemo(() => {
    if (!tools) return [];
    if (!search.trim()) return tools;
    const q = search.toLowerCase();
    return tools.filter(
      (t) => t.name.toLowerCase().includes(q) || t.description.toLowerCase().includes(q)
    );
  }, [tools, search]);

  if (isLoading) {
    return <ToolListSkeleton />;
  }

  if (isError) {
    return (
      <EmptyState
        icon={<Wrench size={32} />}
        title="Failed to load tools"
        description="Unable to connect to the MCP server. Check that the agent hub is running."
      />
    );
  }

  if (!tools?.length) {
    return (
      <EmptyState
        icon={<Wrench size={32} />}
        title="No tools available"
        description="No MCP tools are registered. Configure tool servers in your agent settings."
      />
    );
  }

  return (
    <div className="grid grid-cols-1 lg:grid-cols-[320px_1fr] h-full overflow-hidden">
      {/* Tool List Panel */}
      <div className="flex flex-col border-r overflow-hidden">
        <div className="p-3 border-b">
          <div className="flex items-center gap-2 mb-2">
            <h2 className="text-sm font-medium">Tools</h2>
            <Badge variant="secondary" className="text-[10px]">{filteredTools.length}</Badge>
          </div>
          <div className="relative">
            <Search size={14} className="absolute left-2.5 top-1/2 -translate-y-1/2 text-muted-foreground" />
            <Input
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              placeholder="Search tools..."
              className="pl-8 h-7 text-xs"
            />
          </div>
        </div>

        <div className="flex-1 overflow-y-auto">
          {filteredTools.length === 0 ? (
            <div className="p-4 text-center text-xs text-muted-foreground">
              No tools match "{search}"
            </div>
          ) : (
            <div className="flex flex-col gap-1 p-2">
              {filteredTools.map((tool) => (
                <button
                  key={tool.name}
                  type="button"
                  onClick={() => setSelectedTool(tool)}
                  className={cn(
                    'group flex items-center gap-2 w-full text-left px-3 py-2.5 rounded-lg transition-colors',
                    'hover:bg-accent/50',
                    selectedTool?.name === tool.name
                      ? 'bg-accent ring-1 ring-accent-foreground/10'
                      : ''
                  )}
                >
                  <Wrench size={14} className="shrink-0 text-muted-foreground group-hover:text-primary transition-colors" />
                  <div className="flex-1 min-w-0">
                    <p className="text-sm font-medium truncate">{tool.name}</p>
                    {tool.description && (
                      <p className="text-[11px] text-muted-foreground truncate mt-0.5">{tool.description}</p>
                    )}
                  </div>
                  <ChevronRight size={12} className="shrink-0 text-muted-foreground opacity-0 group-hover:opacity-100 transition-opacity" />
                </button>
              ))}
            </div>
          )}
        </div>
      </div>

      {/* Tool Detail Panel */}
      <div className="overflow-y-auto p-4">
        {selectedTool ? (
          <ToolDetailPanel tool={selectedTool} />
        ) : (
          <EmptyState
            icon={<Wrench size={32} />}
            title="Select a tool"
            description="Choose a tool from the list to view its schema and invoke it."
          />
        )}
      </div>
    </div>
  );
}
