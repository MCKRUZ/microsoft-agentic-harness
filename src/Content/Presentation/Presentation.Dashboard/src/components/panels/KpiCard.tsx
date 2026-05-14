import { cn } from '@/lib/utils';
import { Sparkline } from '@/components/charts/Sparkline';
import { Info } from 'lucide-react';
import type { MetricDataPoint } from '@/api/types';

interface KpiCardProps {
  title: string;
  value: string;
  unit?: string;
  delta?: string;
  trend?: 'up' | 'down' | 'flat';
  subtitle?: string;
  description?: string;
  sparklineData?: MetricDataPoint[];
  className?: string;
}

const trendColor = {
  up: 'text-otel-positive',
  down: 'text-otel-negative',
  flat: 'text-otel-text-mute',
} as const;

function slugify(text: string): string {
  return text.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/(^-|-$)/g, '');
}

export function KpiCard({ title, value, unit, delta, trend, subtitle, description, sparklineData, className }: KpiCardProps) {
  const testId = `kpi-${slugify(title)}`;
  return (
    <div role="status" aria-label={title} data-testid={testId} className={cn('rounded-xl border border-border bg-card p-4 flex flex-col gap-2', className)}>
      <div className="flex items-center justify-between">
        <span className="text-xs font-medium text-muted-foreground uppercase tracking-wider flex items-center gap-1.5">
          {title}
          {description && (
            <span className="relative group/tip">
              <Info className="w-3 h-3 text-muted-foreground/50 cursor-help" />
              <span className="absolute bottom-full left-1/2 -translate-x-1/2 mb-2 px-3 py-2 text-[11px] font-normal normal-case tracking-normal leading-relaxed text-popover-foreground bg-popover border border-border rounded-lg shadow-lg w-56 opacity-0 pointer-events-none group-hover/tip:opacity-100 group-hover/tip:pointer-events-auto transition-opacity duration-150 z-50">
                {description}
              </span>
            </span>
          )}
        </span>
        {delta && (
          <span className={cn('text-xs font-semibold', trendColor[trend ?? 'flat'])}>
            {delta}
          </span>
        )}
      </div>
      <div className="flex items-end justify-between gap-4">
        <div>
          <span data-testid={`${testId}-value`} className="text-2xl font-bold text-card-foreground">{value}</span>
          {unit && <span className="text-sm text-muted-foreground ml-1">{unit}</span>}
        </div>
        {sparklineData && sparklineData.length > 1 && (
          <div className="w-24">
            <Sparkline dataPoints={sparklineData} />
          </div>
        )}
      </div>
      {subtitle && (
        <span className="text-[11px] text-otel-text-dim">{subtitle}</span>
      )}
    </div>
  );
}
