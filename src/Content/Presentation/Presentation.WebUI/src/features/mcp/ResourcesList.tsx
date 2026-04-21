import { useState, useMemo, useCallback } from 'react';
import { FileText, Search, Copy, Check, ExternalLink } from 'lucide-react';
import { useResourcesQuery } from './useMcpQuery';
import { Card, CardHeader, CardTitle, CardDescription, CardContent } from '@/components/ui/card';
import { Badge } from '@/components/ui/badge';
import { Input } from '@/components/ui/input';
import { EmptyState } from '@/components/ui/empty-state';

function CopyUriButton({ uri }: { uri: string }) {
  const [copied, setCopied] = useState(false);

  const handleCopy = useCallback(async () => {
    try {
      await navigator.clipboard.writeText(uri);
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    } catch { /* clipboard unavailable */ }
  }, [uri]);

  return (
    <button
      type="button"
      onClick={() => { void handleCopy(); }}
      aria-label={copied ? 'Copied URI' : 'Copy URI'}
      className="inline-flex items-center gap-1 px-2 py-1 text-[10px] rounded border border-border text-muted-foreground hover:text-foreground hover:bg-accent transition-colors"
    >
      {copied ? <Check size={10} /> : <Copy size={10} />}
      {copied ? 'Copied' : 'Copy URI'}
    </button>
  );
}

function getMimeType(uri: string): string | null {
  const ext = uri.split('.').pop()?.toLowerCase();
  const mimeMap: Record<string, string> = {
    json: 'application/json',
    md: 'text/markdown',
    txt: 'text/plain',
    yaml: 'text/yaml',
    yml: 'text/yaml',
    xml: 'application/xml',
    html: 'text/html',
    css: 'text/css',
    js: 'application/javascript',
    ts: 'application/typescript',
  };
  return ext ? (mimeMap[ext] ?? null) : null;
}

function ResourcesSkeleton() {
  return (
    <div className="grid gap-3 p-4">
      {Array.from({ length: 4 }).map((_, i) => (
        <div key={i} className="h-20 rounded-xl bg-muted/50 animate-pulse" />
      ))}
    </div>
  );
}

export function ResourcesList() {
  const { data: resources, isLoading, isError } = useResourcesQuery();
  const [search, setSearch] = useState('');

  const filteredResources = useMemo(() => {
    if (!resources) return [];
    if (!search.trim()) return resources;
    const q = search.toLowerCase();
    return resources.filter(
      (r) =>
        r.name.toLowerCase().includes(q) ||
        r.uri.toLowerCase().includes(q) ||
        (r.description?.toLowerCase().includes(q) ?? false)
    );
  }, [resources, search]);

  if (isLoading) {
    return <ResourcesSkeleton />;
  }

  if (isError) {
    return (
      <EmptyState
        icon={<FileText size={32} />}
        title="Failed to load resources"
        description="Unable to connect to the MCP server. Check that the agent hub is running."
      />
    );
  }

  if (!resources?.length) {
    return (
      <EmptyState
        icon={<FileText size={32} />}
        title="No resources available"
        description="No MCP resources are registered. Configure resource servers in your agent settings."
      />
    );
  }

  return (
    <div className="flex flex-col h-full overflow-hidden">
      {/* Search Header */}
      <div className="p-4 border-b">
        <div className="flex items-center gap-2 mb-3">
          <h2 className="text-sm font-medium">Resources</h2>
          <Badge variant="secondary" className="text-[10px]">{filteredResources.length}</Badge>
        </div>
        <div className="relative">
          <Search size={14} className="absolute left-2.5 top-1/2 -translate-y-1/2 text-muted-foreground" />
          <Input
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            placeholder="Search resources..."
            className="pl-8 h-7 text-xs"
          />
        </div>
      </div>

      {/* Resource Cards */}
      <div className="flex-1 overflow-y-auto p-4">
        {filteredResources.length === 0 ? (
          <div className="text-center text-xs text-muted-foreground py-8">
            No resources match "{search}"
          </div>
        ) : (
          <div className="grid gap-3">
            {filteredResources.map((resource) => {
              const mime = getMimeType(resource.uri);
              return (
                <Card key={resource.uri} size="sm">
                  <CardHeader>
                    <div className="flex items-start justify-between gap-2">
                      <div className="flex items-center gap-2 min-w-0">
                        <FileText size={14} className="shrink-0 text-primary" />
                        <CardTitle className="truncate">{resource.name}</CardTitle>
                      </div>
                      <CopyUriButton uri={resource.uri} />
                    </div>
                    {resource.description && (
                      <CardDescription className="text-xs">{resource.description}</CardDescription>
                    )}
                  </CardHeader>
                  <CardContent>
                    <div className="flex items-center gap-2 flex-wrap">
                      <Badge variant="outline" className="text-[10px] font-mono max-w-[280px] truncate">
                        <ExternalLink size={9} className="shrink-0 mr-1" />
                        {resource.uri}
                      </Badge>
                      {mime && (
                        <Badge variant="secondary" className="text-[10px]">
                          {mime}
                        </Badge>
                      )}
                    </div>
                  </CardContent>
                </Card>
              );
            })}
          </div>
        )}
      </div>
    </div>
  );
}
