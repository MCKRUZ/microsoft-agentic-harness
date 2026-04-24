import { cn } from '@/lib/utils';

interface LoadingSkeletonProps {
  className?: string;
}

export function LoadingSkeleton({ className }: LoadingSkeletonProps) {
  return (
    <div className={cn('animate-pulse rounded-xl border border-border bg-card p-5', className)}>
      <div className="h-3 w-24 bg-muted rounded mb-4" />
      <div className="h-[200px] bg-muted rounded" />
    </div>
  );
}
