import { cn } from '@/lib/utils';

// `sparkData?: number[]` and `sparkColor?: string` used to live here, rendering
// <Sparkline data={sparkData} .../>. Sparkline takes `dataPoints: MetricDataPoint[]`, not
// `data`, so the required prop arrived undefined and the component threw on
// `dataPoints.map(...)`. No caller ever passed sparkData, which is the only reason it never
// crashed in production — and also why the typecheck that would have caught it was a no-op.
// Removed rather than repaired: MetricDataPoint requires a timestamp, so adapting number[]
// would mean fabricating one. Callers that want a sparkline use <Sparkline> directly, as
// SloBoard.tsx already does.
interface KpiProps {
  label: string;
  value: string | number;
  unit?: string;
  delta?: number;
  deltaGood?: 'up' | 'down';
  narrative?: string;
  className?: string;
}

function formatDelta(n: number): string {
  return (n >= 0 ? '+' : '') + (n * 100).toFixed(1) + '%';
}

export function Kpi({ label, value, unit, delta, deltaGood, narrative, className }: KpiProps) {
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
      {narrative && (
        <div className="text-[11px] text-otel-text-dim leading-snug">{narrative}</div>
      )}
    </div>
  );
}
