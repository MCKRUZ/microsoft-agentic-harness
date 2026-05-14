import { cn } from '@/lib/utils';

interface StatusDotProps {
  status: 'active' | 'completed' | 'errored' | 'ok' | 'warning' | 'critical';
  size?: number;
  className?: string;
}

const statusColors: Record<StatusDotProps['status'], string> = {
  active: 'bg-green-500',
  completed: 'bg-slate-400',
  errored: 'bg-red-500',
  ok: 'bg-green-500',
  warning: 'bg-amber-500',
  critical: 'bg-red-500',
};

export function StatusDot({ status, size = 6, className }: StatusDotProps) {
  return (
    <span
      className={cn('inline-block rounded-full shrink-0', statusColors[status], className)}
      style={{ width: size, height: size }}
    />
  );
}
