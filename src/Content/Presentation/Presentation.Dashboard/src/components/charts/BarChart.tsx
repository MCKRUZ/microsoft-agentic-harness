import { ResponsiveContainer, BarChart as RechartsBarChart, Bar, XAxis, YAxis, Tooltip, CartesianGrid } from 'recharts';
import { getChartColor } from './chartTheme';
import type { MetricSeries } from '@/api/types';

interface MetricBarChartProps {
  series: MetricSeries[];
  unit?: string;
  layout?: 'vertical' | 'horizontal';
}

function formatValue(value: string, unit?: string): string {
  const num = parseFloat(value);
  if (isNaN(num)) return value;
  if (unit === 'usd') return `$${num.toFixed(4)}`;
  if (unit === 'percent') return `${(num * 100).toFixed(1)}%`;
  if (unit === 'ms') return `${(num * 1000).toFixed(0)}ms`;
  if (num >= 1000000) return `${(num / 1000000).toFixed(1)}M`;
  if (num >= 1000) return `${(num / 1000).toFixed(1)}K`;
  return num.toFixed(2);
}

export function MetricBarChart({ series, unit, layout = 'horizontal' }: MetricBarChartProps) {
  if (series.length === 0) return <div className="text-muted-foreground text-sm">No data</div>;

  const chartData = series.map((s, i) => {
    const label = s.labels['__name__'] ?? s.labels['model'] ?? s.labels['tool'] ?? `series_${i}`;
    const value = parseFloat(s.dataPoints[s.dataPoints.length - 1]?.value ?? '0') || 0;
    return { name: label, value };
  });

  if (layout === 'vertical') {
    return (
      <ResponsiveContainer width="100%" height={Math.max(200, chartData.length * 36)}>
        <RechartsBarChart data={chartData} layout="vertical" margin={{ left: 80 }}>
          <CartesianGrid strokeDasharray="3 3" stroke="var(--border)" horizontal={false} />
          <XAxis type="number" tickFormatter={(v: number) => formatValue(String(v), unit)} stroke="var(--muted-foreground)" fontSize={11} />
          <YAxis type="category" dataKey="name" stroke="var(--muted-foreground)" fontSize={11} width={75} />
          <Tooltip contentStyle={{ background: 'var(--card)', border: '1px solid var(--border)', borderRadius: '8px' }} formatter={(v: number) => [formatValue(String(v), unit), '']} />
          <Bar dataKey="value" fill={getChartColor(0)} radius={[0, 4, 4, 0]} />
        </RechartsBarChart>
      </ResponsiveContainer>
    );
  }

  return (
    <ResponsiveContainer width="100%" height={200}>
      <RechartsBarChart data={chartData}>
        <CartesianGrid strokeDasharray="3 3" stroke="var(--border)" />
        <XAxis dataKey="name" stroke="var(--muted-foreground)" fontSize={11} />
        <YAxis tickFormatter={(v: number) => formatValue(String(v), unit)} stroke="var(--muted-foreground)" fontSize={11} width={60} />
        <Tooltip contentStyle={{ background: 'var(--card)', border: '1px solid var(--border)', borderRadius: '8px' }} formatter={(v: number) => [formatValue(String(v), unit), '']} />
        <Bar dataKey="value" fill={getChartColor(0)} radius={[4, 4, 0, 0]} />
      </RechartsBarChart>
    </ResponsiveContainer>
  );
}
