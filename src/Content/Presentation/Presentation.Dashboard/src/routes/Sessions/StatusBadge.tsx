import { cn } from '@/lib/utils';

interface StatusBadgeProps {
  status: string;
  className?: string;
}

const statusStyles: Record<string, string> = {
  completed: 'bg-emerald-500/15 text-emerald-400',
  error: 'bg-red-500/15 text-red-400',
  active: 'bg-amber-500/15 text-amber-400',
};

export function StatusBadge({ status, className }: StatusBadgeProps) {
  const style = statusStyles[status.toLowerCase()] ?? 'bg-muted text-muted-foreground';
  return (
    <span className={cn('inline-block rounded-full px-2 py-0.5 text-xs font-medium capitalize', style, className)}>
      {status}
    </span>
  );
}
