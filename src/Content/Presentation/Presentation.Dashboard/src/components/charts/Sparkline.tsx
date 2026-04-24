import { ResponsiveContainer, LineChart, Line } from 'recharts';
import type { MetricDataPoint } from '@/api/types';

interface SparklineProps {
  dataPoints: MetricDataPoint[];
  color?: string;
  height?: number;
}

export function Sparkline({ dataPoints, color = 'var(--chart-1)', height = 32 }: SparklineProps) {
  const data = dataPoints.map((dp) => ({
    v: parseFloat(dp.value) || 0,
  }));

  return (
    <ResponsiveContainer width="100%" height={height}>
      <LineChart data={data}>
        <Line type="monotone" dataKey="v" stroke={color} dot={false} strokeWidth={1.5} />
      </LineChart>
    </ResponsiveContainer>
  );
}
