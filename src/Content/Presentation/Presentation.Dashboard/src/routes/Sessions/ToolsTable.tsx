import { Link, useParams } from 'react-router';
import type { ToolExecutionRecord } from '@/api/types';
import { StatusBadge } from './StatusBadge';
import { formatTokens } from './format';

interface ToolsTableProps {
  tools: ToolExecutionRecord[];
}

export function ToolsTable({ tools }: ToolsTableProps) {
  // sessionId comes from the parent SessionDetailPage route — the table is
  // only rendered there, so the param is always defined when this component
  // is on screen.
  const { sessionId } = useParams<{ sessionId: string }>();
  if (tools.length === 0) return null;

  return (
    <div className="overflow-x-auto">
      <table className="w-full text-sm">
        <thead>
          <tr className="border-b border-border text-left text-xs font-medium text-muted-foreground uppercase tracking-wider">
            <th className="pb-2 pr-4">Tool Name</th>
            <th className="pb-2 pr-4">Source</th>
            <th className="pb-2 pr-4 text-right">Duration</th>
            <th className="pb-2 pr-4">Status</th>
            <th className="pb-2 pr-4">Error</th>
            <th className="pb-2 text-right">Result Size</th>
          </tr>
        </thead>
        <tbody>
          {tools.map((t) => (
            <tr key={t.id} className="border-b border-border/50">
              <td className="py-2 pr-4 font-mono text-card-foreground">
                {sessionId ? (
                  <Link
                    to={`/sessions/${sessionId}/tools/${t.id}`}
                    className="hover:text-cat-accent hover:underline"
                  >
                    {t.toolName}
                  </Link>
                ) : (
                  t.toolName
                )}
              </td>
              <td className="py-2 pr-4 text-muted-foreground">{t.toolSource ?? '--'}</td>
              <td className="py-2 pr-4 text-right text-muted-foreground">
                {t.durationMs !== null ? `${t.durationMs}ms` : '--'}
              </td>
              <td className="py-2 pr-4"><StatusBadge status={t.status} /></td>
              <td className="py-2 pr-4 text-red-400 text-xs">{t.errorType ?? ''}</td>
              <td className="py-2 text-right text-muted-foreground">
                {t.resultSize !== null ? formatTokens(t.resultSize) : '--'}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
