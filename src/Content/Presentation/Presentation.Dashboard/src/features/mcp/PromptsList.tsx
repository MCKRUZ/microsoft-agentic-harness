import { useState, useMemo, useCallback } from 'react';
import { MessageSquare, Search, Copy, Check } from 'lucide-react';
import { usePromptsQuery } from './useMcpQuery';
import { Card, CardHeader, CardTitle, CardDescription, CardContent } from '@/components/ui/card';
import { Badge } from '@/components/ui/badge';
import { Input } from '@/components/ui/input';
import { EmptyState } from '@/components/ui/empty-state';

function CopyButton({ text }: { text: string }) {
  const [copied, setCopied] = useState(false);

  const handleCopy = useCallback(async () => {
    try {
      await navigator.clipboard.writeText(text);
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    } catch { /* clipboard unavailable */ }
  }, [text]);

  return (
    <button
      type="button"
      onClick={() => { void handleCopy(); }}
      aria-label={copied ? 'Copied' : 'Copy'}
      className="inline-flex items-center gap-1 px-2 py-1 text-[10px] rounded border border-border text-muted-foreground hover:text-foreground hover:bg-accent transition-colors"
    >
      {copied ? <Check size={10} /> : <Copy size={10} />}
      {copied ? 'Copied' : 'Copy'}
    </button>
  );
}

function PromptsSkeleton() {
  return (
    <div className="grid gap-3 p-4">
      {Array.from({ length: 4 }).map((_, i) => (
        <div key={i} className="h-24 rounded-xl bg-muted/50 animate-pulse" />
      ))}
    </div>
  );
}

export function PromptsList() {
  const { data: prompts, isLoading, isError } = usePromptsQuery();
  const [search, setSearch] = useState('');

  const filteredPrompts = useMemo(() => {
    if (!prompts) return [];
    if (!search.trim()) return prompts;
    const q = search.toLowerCase();
    return prompts.filter(
      (p) =>
        p.name.toLowerCase().includes(q) ||
        (p.description?.toLowerCase().includes(q) ?? false)
    );
  }, [prompts, search]);

  if (isLoading) {
    return <PromptsSkeleton />;
  }

  if (isError) {
    return (
      <EmptyState
        icon={<MessageSquare size={32} />}
        title="Failed to load prompts"
        description="Unable to connect to the MCP server. Check that the agent hub is running."
      />
    );
  }

  if (!prompts?.length) {
    return (
      <EmptyState
        icon={<MessageSquare size={32} />}
        title="No prompts available"
        description="No MCP prompt templates are registered. Configure prompt servers in your agent settings."
      />
    );
  }

  return (
    <div className="flex flex-col h-full overflow-hidden">
      {/* Search Header */}
      <div className="p-4 border-b">
        <div className="flex items-center gap-2 mb-3">
          <h2 className="text-sm font-medium">Prompt Templates</h2>
          <Badge variant="secondary" className="text-[10px]">{filteredPrompts.length}</Badge>
        </div>
        <div className="relative">
          <Search size={14} className="absolute left-2.5 top-1/2 -translate-y-1/2 text-muted-foreground" />
          <Input
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            placeholder="Search prompts..."
            className="pl-8 h-7 text-xs"
          />
        </div>
      </div>

      {/* Prompt Cards */}
      <div className="flex-1 overflow-y-auto p-4">
        {filteredPrompts.length === 0 ? (
          <div className="text-center text-xs text-muted-foreground py-8">
            No prompts match "{search}"
          </div>
        ) : (
          <div className="grid gap-3">
            {filteredPrompts.map((prompt) => (
              <Card key={prompt.name} size="sm">
                <CardHeader>
                  <div className="flex items-start justify-between gap-2">
                    <div className="flex items-center gap-2 min-w-0">
                      <MessageSquare size={14} className="shrink-0 text-primary" />
                      <CardTitle className="truncate">{prompt.name}</CardTitle>
                    </div>
                    <CopyButton text={prompt.name} />
                  </div>
                  {prompt.description && (
                    <CardDescription className="text-xs">{prompt.description}</CardDescription>
                  )}
                </CardHeader>
                {prompt.arguments && prompt.arguments.length > 0 && (
                  <CardContent>
                    <div className="flex flex-col gap-1.5">
                      <span className="text-[10px] font-medium uppercase tracking-wide text-muted-foreground">Arguments</span>
                      <div className="flex flex-wrap gap-1.5">
                        {prompt.arguments.map((arg) => (
                          <Badge
                            key={arg.name}
                            variant={arg.required ? 'default' : 'outline'}
                            className="text-[10px] font-mono"
                          >
                            {arg.name}
                            {arg.required === false && <span className="ml-0.5 opacity-60">?</span>}
                          </Badge>
                        ))}
                      </div>
                    </div>
                  </CardContent>
                )}
              </Card>
            ))}
          </div>
        )}
      </div>
    </div>
  );
}
