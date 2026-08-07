import { cn } from '@/lib/utils';

interface StatusBadgeProps {
  status: string;
  className?: string;
}

// Must cover every value of SessionStatus. A status with no entry here does not fail — it falls
// through to the neutral "unknown" style below and looks like a rendering glitch rather than a
// missing case, which is how 'cancelled' would have shipped looking broken.
//
// Slate rather than amber for cancelled: amber is taken by active, and the two must not read as
// near-identical when a cancelled run is precisely the one that is NOT still going.
const statusStyles: Record<string, string> = {
  completed: 'bg-emerald-500/15 text-emerald-400',
  error: 'bg-red-500/15 text-red-400',
  active: 'bg-amber-500/15 text-amber-400',
  cancelled: 'bg-slate-500/15 text-slate-400',
};

export function StatusBadge({ status, className }: StatusBadgeProps) {
  const style = statusStyles[status.toLowerCase()] ?? 'bg-muted text-muted-foreground';
  return (
    <span className={cn('inline-block rounded-full px-2 py-0.5 text-xs font-medium capitalize', style, className)}>
      {status}
    </span>
  );
}
