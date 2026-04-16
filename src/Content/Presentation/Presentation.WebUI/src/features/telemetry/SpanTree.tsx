import type { SpanTreeNode } from './types';
import { SpanNode } from './SpanNode';

interface SpanTreeProps {
  node: SpanTreeNode;
}

export function SpanTree({ node }: SpanTreeProps) {
  return (
    <div data-testid="span-tree" className="border-b border-border/30 py-1 last:border-0">
      <SpanNode node={node} rootDurationMs={node.durationMs} depth={0} />
    </div>
  );
}
