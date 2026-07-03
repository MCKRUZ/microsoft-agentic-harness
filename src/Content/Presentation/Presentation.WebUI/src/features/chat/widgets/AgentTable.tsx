import { parseTableArgs } from './tableTypes';

/**
 * Renders an agent-supplied data table inline in the transcript. Re-validates the arguments at render
 * time (the agent's output is untrusted) and shows a safe fallback for a spec with no columns rather
 * than a broken table. Every cell is plain text — React escapes it — so the agent chooses the data,
 * never the markup. Wide tables scroll horizontally inside their own container so the transcript never
 * scrolls sideways.
 */
export function AgentTable({ args }: { args: Record<string, unknown> }) {
  const result = parseTableArgs(args);

  if (!result.ok) {
    return (
      <div
        data-testid="agent-table-fallback"
        className="rounded-lg border border-border/50 bg-muted/40 px-3 py-2 text-xs text-muted-foreground"
      >
        {result.reason}
      </div>
    );
  }

  const { title, columns, rows } = result.value;
  return (
    <figure
      data-testid="agent-table"
      className="rounded-lg border border-border/50 bg-card/50 overflow-hidden max-w-full"
    >
      {title && (
        <figcaption className="px-3 py-2 text-sm font-medium text-foreground border-b border-border/50">
          {title}
        </figcaption>
      )}
      <div className="overflow-x-auto">
        <table className="w-full border-collapse text-xs">
          <thead>
            <tr className="border-b border-border/50 bg-muted/30">
              {columns.map((col, i) => (
                <th key={i} scope="col" className="px-3 py-2 text-left font-medium text-foreground whitespace-nowrap">
                  {col}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {rows.length === 0 ? (
              <tr>
                <td colSpan={columns.length} className="px-3 py-2 text-center text-muted-foreground">
                  No rows
                </td>
              </tr>
            ) : (
              rows.map((row, r) => (
                <tr key={r} className="border-b border-border/30 last:border-b-0">
                  {row.map((cell, c) => (
                    <td key={c} className="px-3 py-2 text-muted-foreground align-top">
                      {cell}
                    </td>
                  ))}
                </tr>
              ))
            )}
          </tbody>
        </table>
      </div>
    </figure>
  );
}
