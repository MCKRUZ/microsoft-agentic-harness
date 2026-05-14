import { cn } from '@/lib/utils';
import { Sparkline } from '@/components/charts/Sparkline';

interface KpiProps {
  label: string;
  value: string | number;
  unit?: string;
  delta?: number;
  deltaGood?: 'up' | 'down';
  sparkData?: number[];
  sparkColor?: string;
  narrative?: string;
  className?: string;
}

function formatDelta(n: number): string {
  return (n >= 0 ? '+' : '') + (n * 100).toFixed(1) + '%';
}

export function Kpi({ label, value, unit, delta, deltaGood, sparkData, sparkColor, narrative, className }: KpiProps) {
  const isPositive = deltaGood === 'up' ? (delta ?? 0) > 0 : (delta ?? 0) < 0;
  const deltaColor = delta === undefined || delta === 0
    ? 'text-otel-text-mute'
    : isPositive ? 'text-otel-positive' : 'text-otel-negative';

  return (
    <div className={cn('bg-card border border-border rounded-lg p-3.5 flex flex-col gap-2 min-h-[104px]', className)}>
      <div className="flex justify-between items-start">
        <span className="text-[10px] text-otel-text-mute tracking-[0.12em] uppercase font-semibold">
          {label}
        </span>
        {delta !== undefined && (
          <span className={cn('text-[10px] font-mono tabular-nums', deltaColor)}>
            {formatDelta(delta)}
          </span>
        )}
      </div>
      <div className="flex items-end gap-1">
        <span className="text-2xl font-bold text-foreground leading-none tabular-nums">
          {value}
        </span>
        {unit && <span className="text-[11px] text-otel-text-mute mb-0.5">{unit}</span>}
      </div>
      {sparkData && (
        <Sparkline data={sparkData} color={sparkColor ?? 'var(--otel-accent)'} height={28} />
      )}
      {narrative && (
        <div className="text-[11px] text-otel-text-dim leading-snug">{narrative}</div>
      )}
    </div>
  );
}
