import { ResponsiveContainer, PieChart as RechartsPieChart, Pie, Cell, Tooltip, Legend } from 'recharts';
import { getChartColor } from './chartTheme';
import type { MetricSeries } from '@/api/types';

interface MetricPieChartProps {
  series: MetricSeries[];
  unit?: string;
}

function formatValue(value: number, unit?: string): string {
  if (unit === 'usd') return `$${value.toFixed(4)}`;
  if (unit === 'percent') return `${(value * 100).toFixed(1)}%`;
  if (value >= 1000000) return `${(value / 1000000).toFixed(1)}M`;
  if (value >= 1000) return `${(value / 1000).toFixed(1)}K`;
  return value.toFixed(0);
}

export function MetricPieChart({ series, unit }: MetricPieChartProps) {
  if (series.length === 0) return <div className="text-muted-foreground text-sm">No data</div>;

  const data = series.map((s, i) => {
    const label = s.labels['__name__'] ?? s.labels['category'] ?? s.labels['model'] ?? `slice_${i}`;
    const value = parseFloat(s.dataPoints[s.dataPoints.length - 1]?.value ?? '0') || 0;
    return { name: label, value };
  }).filter((d) => d.value > 0);

  if (data.length === 0) return <div className="text-muted-foreground text-sm">No data</div>;

  return (
    <ResponsiveContainer width="100%" height={250}>
      <RechartsPieChart>
        <Pie data={data} cx="50%" cy="50%" innerRadius={50} outerRadius={90} paddingAngle={2} dataKey="value" label={({ name, value }) => `${name}: ${formatValue(value, unit)}`}>
          {data.map((_, i) => (
            <Cell key={i} fill={getChartColor(i)} />
          ))}
        </Pie>
        <Tooltip contentStyle={{ background: 'var(--card)', border: '1px solid var(--border)', borderRadius: '8px' }} formatter={(v: number) => [formatValue(v, unit), '']} />
        <Legend />
      </RechartsPieChart>
    </ResponsiveContainer>
  );
}
