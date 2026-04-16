import { useState } from 'react';
import type { SpanTreeNode } from './types';
import { SpanDetail } from './SpanDetail';

const MAX_DEPTH = 20;

const STATUS_DOT: Record<string, string> = {
  ok: 'bg-green-500',
  error: 'bg-red-500',
  unset: 'bg-gray-400',
};

interface SpanNodeProps {
  node: SpanTreeNode;
  rootDurationMs: number;
  depth?: number;
}

export function SpanNode({ node, rootDurationMs, depth = 0 }: SpanNodeProps) {
  const [expanded, setExpanded] = useState(false);

  if (depth >= MAX_DEPTH) {
    return <div className="px-1 py-0.5 text-xs text-muted-foreground italic">…(max depth)</div>;
  }
  const dotClass = STATUS_DOT[node.status] ?? 'bg-gray-400';
  const barWidth = rootDurationMs > 0 ? Math.min(100, (node.durationMs / rootDurationMs) * 100) : 0;

  return (
    <div style={{ marginLeft: `${depth * 16}px` }}>
      <button
        type="button"
        onClick={() => { setExpanded((v) => !v); }}
        className="flex items-center gap-2 w-full text-left px-1 py-0.5 rounded hover:bg-accent text-xs"
      >
        <span className={`shrink-0 w-2 h-2 rounded-full ${dotClass}`} />
        <div className="relative flex-1 min-w-0">
          <div
            className={`absolute inset-y-0 left-0 rounded opacity-20 ${dotClass}`}
            style={{ width: `${barWidth}%` }}
          />
          <span className="relative truncate">{node.name}</span>
        </div>
        <span className="shrink-0 text-muted-foreground">({node.durationMs}ms)</span>
      </button>

      {expanded && <SpanDetail span={node} />}

      {expanded && node.children.map((child) => (
        <SpanNode
          key={child.spanId}
          node={child}
          rootDurationMs={rootDurationMs}
          depth={depth + 1}
        />
      ))}
    </div>
  );
}
