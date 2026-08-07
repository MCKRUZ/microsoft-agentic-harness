import { cn } from '@/lib/utils';

interface StatusBadgeProps {
  status: string;
  className?: string;
}

// Every value the API's SessionStatus can take. Keep this in step with the C# enum of the same name
// and with the sessions.status CHECK constraint in Infrastructure.Observability/Migrations.
export type SessionStatus = 'active' | 'completed' | 'error' | 'cancelled';

// Keyed by SessionStatus, not by string, and that is the whole point: a value added to the union
// with no style here is a compile error under `tsc --noEmit`, which the frontend CI runs. Typed as
// Record<string, string> it was not — a missing entry fell through to the neutral style below and
// rendered as a washed-out badge that reads like a CSS glitch rather than a missing case. The
// previous version of this comment asked the reader to remember instead, on the one site in this
// vocabulary where the type system was available to enforce it.
//
// Slate rather than amber for cancelled: amber is taken by active, and the two must not read as
// near-identical when a cancelled run is precisely the one that is NOT still going.
const statusStyles: Record<SessionStatus, string> = {
  completed: 'bg-emerald-500/15 text-emerald-400',
  error: 'bg-red-500/15 text-red-400',
  active: 'bg-amber-500/15 text-amber-400',
  cancelled: 'bg-slate-500/15 text-slate-400',
};

export function StatusBadge({ status, className }: StatusBadgeProps) {
  // The runtime fallback stays. The Record type makes a status the UI forgot a build error, but the
  // status arrives over HTTP from a server that may be a version ahead, and an unstyled badge beats
  // a thrown render.
  const style =
    statusStyles[status.toLowerCase() as SessionStatus] ?? 'bg-muted text-muted-foreground';
  return (
    <span className={cn('inline-block rounded-full px-2 py-0.5 text-xs font-medium capitalize', style, className)}>
      {status}
    </span>
  );
}
