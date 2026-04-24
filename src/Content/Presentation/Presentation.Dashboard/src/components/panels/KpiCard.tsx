import { cn } from '@/lib/utils';
import { Sparkline } from '@/components/charts/Sparkline';
import type { MetricDataPoint } from '@/api/types';

interface KpiCardProps {
  title: string;
  value: string;
  unit?: string;
  sparklineData?: MetricDataPoint[];
  className?: string;
}

export function KpiCard({ title, value, unit, sparklineData, className }: KpiCardProps) {
  return (
    <div role="status" aria-label={title} className={cn('rounded-xl border border-border bg-card p-4 flex flex-col gap-2', className)}>
      <span className="text-xs font-medium text-muted-foreground uppercase tracking-wider">
        {title}
      </span>
      <div className="flex items-end justify-between gap-4">
        <div>
          <span className="text-2xl font-bold text-card-foreground">{value}</span>
          {unit && <span className="text-sm text-muted-foreground ml-1">{unit}</span>}
        </div>
        {sparklineData && sparklineData.length > 1 && (
          <div className="w-24">
            <Sparkline dataPoints={sparklineData} />
          </div>
        )}
      </div>
    </div>
  );
}
