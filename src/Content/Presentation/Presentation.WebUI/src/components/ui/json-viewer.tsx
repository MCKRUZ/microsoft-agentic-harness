import { useState, useCallback } from 'react';
import { Check, ChevronDown, ChevronRight, Copy } from 'lucide-react';
import { cn } from '@/lib/utils';

interface JsonViewerProps {
  data: unknown;
  className?: string;
  defaultExpanded?: boolean;
  maxInitialDepth?: number;
}

export function JsonViewer({ data, className, defaultExpanded = true, maxInitialDepth = 2 }: JsonViewerProps) {
  const [copied, setCopied] = useState(false);

  const handleCopy = useCallback(async () => {
    try {
      await navigator.clipboard.writeText(JSON.stringify(data, null, 2));
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    } catch { /* clipboard unavailable */ }
  }, [data]);

  return (
    <div className={cn('group/json relative rounded-md border bg-muted/30 p-3 font-mono text-xs', className)}>
      <button
        type="button"
        onClick={() => { void handleCopy(); }}
        aria-label={copied ? 'Copied' : 'Copy JSON'}
        className="absolute top-2 right-2 rounded p-1 text-muted-foreground opacity-0 group-hover/json:opacity-100 focus:opacity-100 hover:text-foreground transition-opacity"
      >
        {copied ? <Check size={12} /> : <Copy size={12} />}
      </button>
      <div className="overflow-auto max-h-[400px]">
        <JsonNode value={data} depth={0} defaultExpanded={defaultExpanded} maxInitialDepth={maxInitialDepth} />
      </div>
    </div>
  );
}

function JsonNode({ value, depth, defaultExpanded, maxInitialDepth }: {
  value: unknown;
  depth: number;
  defaultExpanded: boolean;
  maxInitialDepth: number;
}) {
  if (value === null) return <span className="text-orange-400">null</span>;
  if (value === undefined) return <span className="text-orange-400">undefined</span>;
  if (typeof value === 'boolean') return <span className="text-orange-400">{String(value)}</span>;
  if (typeof value === 'number') return <span className="text-blue-400">{value}</span>;
  if (typeof value === 'string') {
    if (value.length > 200) {
      return <TruncatedString value={value} />;
    }
    return <span className="text-emerald-400">"{value}"</span>;
  }

  if (Array.isArray(value)) {
    if (value.length === 0) return <span className="text-muted-foreground">[]</span>;
    return (
      <CollapsibleNode
        label={`Array(${value.length})`}
        bracketOpen="["
        bracketClose="]"
        depth={depth}
        defaultExpanded={defaultExpanded && depth < maxInitialDepth}
      >
        {value.map((item, i) => (
          <div key={i} className="flex" style={{ paddingLeft: 16 }}>
            <span className="text-muted-foreground select-none mr-1">{i}:</span>
            <JsonNode value={item} depth={depth + 1} defaultExpanded={defaultExpanded} maxInitialDepth={maxInitialDepth} />
            {i < value.length - 1 && <span className="text-muted-foreground">,</span>}
          </div>
        ))}
      </CollapsibleNode>
    );
  }

  if (typeof value === 'object') {
    const entries = Object.entries(value);
    if (entries.length === 0) return <span className="text-muted-foreground">{'{}'}</span>;
    return (
      <CollapsibleNode
        label={`{${entries.length}}`}
        bracketOpen="{"
        bracketClose="}"
        depth={depth}
        defaultExpanded={defaultExpanded && depth < maxInitialDepth}
      >
        {entries.map(([key, val], i) => (
          <div key={key} className="flex flex-wrap" style={{ paddingLeft: 16 }}>
            <span className="text-purple-400">"{key}"</span>
            <span className="text-muted-foreground mr-1">:</span>
            <JsonNode value={val} depth={depth + 1} defaultExpanded={defaultExpanded} maxInitialDepth={maxInitialDepth} />
            {i < entries.length - 1 && <span className="text-muted-foreground">,</span>}
          </div>
        ))}
      </CollapsibleNode>
    );
  }

  return <span>{String(value)}</span>;
}

function CollapsibleNode({ label, bracketOpen, bracketClose, depth, defaultExpanded, children }: {
  label: string;
  bracketOpen: string;
  bracketClose: string;
  depth: number;
  defaultExpanded: boolean;
  children: React.ReactNode;
}) {
  const [expanded, setExpanded] = useState(defaultExpanded);

  return (
    <span>
      <button
        type="button"
        onClick={() => setExpanded(e => !e)}
        className="inline-flex items-center gap-0.5 text-muted-foreground hover:text-foreground"
      >
        {expanded ? <ChevronDown size={12} /> : <ChevronRight size={12} />}
        {!expanded && <span className="text-muted-foreground text-[10px]">{label}</span>}
      </button>
      {expanded ? (
        <>
          <span className="text-muted-foreground">{bracketOpen}</span>
          <div>{children}</div>
          <span className="text-muted-foreground" style={{ paddingLeft: depth > 0 ? 0 : undefined }}>{bracketClose}</span>
        </>
      ) : (
        <span className="text-muted-foreground"> {bracketOpen}...{bracketClose}</span>
      )}
    </span>
  );
}

function TruncatedString({ value }: { value: string }) {
  const [expanded, setExpanded] = useState(false);

  return (
    <span className="text-emerald-400">
      "{expanded ? value : value.slice(0, 100)}
      {!expanded && (
        <button
          type="button"
          onClick={() => setExpanded(true)}
          className="text-blue-400 hover:underline mx-1"
        >
          ...({value.length} chars)
        </button>
      )}
      "
    </span>
  );
}
