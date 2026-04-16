import { useMemo } from 'react';
import type { SpanData } from '@/types/signalr';
import { buildSpanTree } from './buildSpanTree';
import { SpanTree } from './SpanTree';

interface TracesPanelProps {
  spans: SpanData[];
  onClear?: () => void;
}

export function TracesPanel({ spans, onClear }: TracesPanelProps) {
  const roots = useMemo(() => buildSpanTree(spans), [spans]);

  return (
    <div className="flex flex-col h-full">
      {onClear && (
        <div className="flex justify-end px-2 py-1 border-b shrink-0">
          <button type="button" onClick={onClear} className="text-xs text-muted-foreground hover:text-foreground">
            Clear
          </button>
        </div>
      )}
      {roots.length === 0 ? (
        <div className="flex-1 flex items-center justify-center text-sm text-muted-foreground p-4 text-center">
          No traces yet. Run an agent turn to see spans here.
        </div>
      ) : (
        <div className="flex-1 overflow-y-auto px-1">
          {roots.map((root) => (
            <SpanTree key={root.spanId} node={root} />
          ))}
        </div>
      )}
    </div>
  );
}
