import { ResponsiveContainer, LineChart, Line, XAxis, YAxis, Tooltip, CartesianGrid } from 'recharts';
import { getChartColor } from './chartTheme';
import type { MetricSeries } from '@/api/types';
import { format } from 'date-fns';

interface TimeSeriesChartProps {
  series: MetricSeries[];
  unit?: string;
  type?: 'line' | 'area';
}

function formatTimestamp(ts: number): string {
  return format(new Date(ts * 1000), 'HH:mm');
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

function seriesName(s: MetricSeries, index: number): string {
  const label = s.labels['model'] ?? s.labels['agent_name'] ?? s.labels['tool_name'];
  return label ?? `series ${index + 1}`;
}

export function TimeSeriesChart({ series, unit }: TimeSeriesChartProps) {
  if (series.length === 0) return <div className="text-muted-foreground text-sm">No data</div>;

  const names = series.map((s, i) => seriesName(s, i));

  const chartData = series[0]?.dataPoints.map((dp, i) => {
    const point: Record<string, string | number> = { time: dp.timestamp };
    series.forEach((s, si) => {
      const val = s.dataPoints[i]?.value ?? '0';
      point[`series_${si}`] = parseFloat(val) || 0;
    });
    return point;
  }) ?? [];

  return (
    <div>
      {series.length > 1 && (
        <div className="flex items-center gap-4 mb-2 px-1">
          {names.map((name, i) => (
            <div key={i} className="flex items-center gap-1.5">
              <span
                className="w-2 h-2 rounded-full inline-block"
                style={{ backgroundColor: getChartColor(i) }}
              />
              <span className="text-[11px] text-otel-text-dim italic">{name}</span>
            </div>
          ))}
        </div>
      )}
      <ResponsiveContainer width="100%" height={200}>
        <LineChart data={chartData}>
          <CartesianGrid strokeDasharray="3 3" stroke="var(--border)" />
          <XAxis
            dataKey="time"
            tickFormatter={formatTimestamp}
            stroke="var(--muted-foreground)"
            fontSize={11}
          />
          <YAxis
            tickFormatter={(v: string) => formatValue(String(v), unit)}
            stroke="var(--muted-foreground)"
            fontSize={11}
            width={60}
          />
          <Tooltip
            contentStyle={{ background: 'var(--card)', border: '1px solid var(--border)', borderRadius: '8px' }}
            labelFormatter={formatTimestamp}
            formatter={(v: number, dataKey: string) => {
              const idx = parseInt(String(dataKey).replace('series_', ''), 10);
              return [formatValue(String(v), unit), names[idx] ?? ''];
            }}
          />
          {series.map((_, i) => (
            <Line
              key={i}
              type="monotone"
              dataKey={`series_${i}`}
              stroke={getChartColor(i)}
              dot={false}
              strokeWidth={2}
            />
          ))}
        </LineChart>
      </ResponsiveContainer>
    </div>
  );
}
