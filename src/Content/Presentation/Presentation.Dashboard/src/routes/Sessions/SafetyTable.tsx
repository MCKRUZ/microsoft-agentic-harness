import type { SafetyEventRecord } from '@/api/types';
import { StatusBadge } from './StatusBadge';

interface SafetyTableProps {
  events: SafetyEventRecord[];
}

export function SafetyTable({ events }: SafetyTableProps) {
  if (events.length === 0) return null;

  return (
    <div className="overflow-x-auto">
      <table className="w-full text-sm">
        <thead>
          <tr className="border-b border-border text-left text-xs font-medium text-muted-foreground uppercase tracking-wider">
            <th className="pb-2 pr-4">Phase</th>
            <th className="pb-2 pr-4">Outcome</th>
            <th className="pb-2 pr-4">Category</th>
            <th className="pb-2 pr-4 text-right">Severity</th>
            <th className="pb-2">Filter</th>
          </tr>
        </thead>
        <tbody>
          {events.map((e) => (
            <tr key={e.id} className="border-b border-border/50">
              <td className="py-2 pr-4 text-card-foreground">{e.phase}</td>
              <td className="py-2 pr-4"><StatusBadge status={e.outcome} /></td>
              <td className="py-2 pr-4 text-muted-foreground">{e.category ?? '--'}</td>
              <td className="py-2 pr-4 text-right">{e.severity ?? '--'}</td>
              <td className="py-2 text-muted-foreground">{e.filterName ?? '--'}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
