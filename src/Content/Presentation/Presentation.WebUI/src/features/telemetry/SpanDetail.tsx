import type { SpanTreeNode } from './types';

interface SpanDetailProps {
  span: SpanTreeNode;
}

export function SpanDetail({ span }: SpanDetailProps) {
  const entries = Object.entries(span.tags);

  return (
    <div className="px-2 py-1 text-xs bg-muted/40 rounded mb-1">
      {span.statusDescription && (
        <p className="text-muted-foreground mb-1 break-all">{span.statusDescription}</p>
      )}
      {entries.length > 0 ? (
        <table className="w-full border-collapse">
          <tbody>
            {entries.map(([key, value]) => (
              <tr key={key} className="border-b border-border/30 last:border-0">
                <td className="py-0.5 pr-3 font-mono text-muted-foreground whitespace-nowrap align-top">
                  {key}
                </td>
                <td className="py-0.5 font-mono break-all">{value}</td>
              </tr>
            ))}
          </tbody>
        </table>
      ) : (
        <span className="text-muted-foreground">No tags</span>
      )}
    </div>
  );
}
