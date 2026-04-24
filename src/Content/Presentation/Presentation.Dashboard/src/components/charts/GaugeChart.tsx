import { ResponsiveContainer, RadialBarChart, RadialBar, PolarAngleAxis } from 'recharts';

interface GaugeChartProps {
  value: number;
  max?: number;
  label?: string;
  unit?: string;
  thresholds?: { warn: number; critical: number };
}

function getColor(value: number, max: number, thresholds?: { warn: number; critical: number }): string {
  if (!thresholds) return 'var(--chart-1)';
  const pct = value / max;
  if (pct >= thresholds.critical / max) return 'var(--chart-5)';
  if (pct >= thresholds.warn / max) return 'var(--chart-3)';
  return 'var(--chart-2)';
}

function formatDisplay(value: number, unit?: string): string {
  if (unit === 'usd') return `$${value.toFixed(2)}`;
  if (unit === 'percent') return `${(value * 100).toFixed(1)}%`;
  if (value >= 1000000) return `${(value / 1000000).toFixed(1)}M`;
  if (value >= 1000) return `${(value / 1000).toFixed(1)}K`;
  return value.toFixed(0);
}

export function GaugeChart({ value, max = 100, label, unit, thresholds }: GaugeChartProps) {
  const fill = getColor(value, max, thresholds);
  const data = [{ value, fill }];

  return (
    <div className="flex flex-col items-center">
      <ResponsiveContainer width="100%" height={160}>
        <RadialBarChart cx="50%" cy="50%" innerRadius="70%" outerRadius="100%" barSize={12} data={data} startAngle={210} endAngle={-30}>
          <PolarAngleAxis type="number" domain={[0, max]} angleAxisId={0} tick={false} />
          <RadialBar background={{ fill: 'var(--muted)' }} dataKey="value" angleAxisId={0} cornerRadius={6} />
        </RadialBarChart>
      </ResponsiveContainer>
      <div className="-mt-16 text-center">
        <div className="text-2xl font-bold text-card-foreground">{formatDisplay(value, unit)}</div>
        {label && <div className="text-xs text-muted-foreground mt-0.5">{label}</div>}
      </div>
    </div>
  );
}
