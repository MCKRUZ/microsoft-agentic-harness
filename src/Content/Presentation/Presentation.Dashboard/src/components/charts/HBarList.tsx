import { cn } from '@/lib/utils';
import { getChartColor } from './chartTheme';

export interface HBarItem {
  label: string;
  value: number;
  formatted: string;
}

interface HBarListProps {
  items: HBarItem[];
  className?: string;
}

export function HBarList({ items, className }: HBarListProps) {
  if (items.length === 0) return <div className="text-muted-foreground text-sm">No data</div>;
  const max = Math.max(...items.map((i) => i.value), 1);

  return (
    <div className={cn('space-y-2', className)}>
      {items.map((item, i) => (
        <div key={item.label} className="flex items-center gap-3">
          <span className="text-xs text-otel-text-dim w-28 shrink-0 truncate" title={item.label}>
            {item.label}
          </span>
          <div className="flex-1 h-5 bg-muted/30 rounded overflow-hidden">
            <div
              className="h-full rounded"
              style={{
                width: `${(item.value / max) * 100}%`,
                backgroundColor: getChartColor(i),
              }}
            />
          </div>
          <span className="text-xs font-mono text-card-foreground w-16 text-right shrink-0">
            {item.formatted}
          </span>
        </div>
      ))}
    </div>
  );
}
