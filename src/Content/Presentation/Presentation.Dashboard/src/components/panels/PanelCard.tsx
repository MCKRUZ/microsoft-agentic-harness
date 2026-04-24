import { cn } from '@/lib/utils';

interface PanelCardProps {
  title: string;
  description?: string;
  children: React.ReactNode;
  className?: string;
}

export function PanelCard({ title, description, children, className }: PanelCardProps) {
  return (
    <div role="region" aria-label={title} className={cn('rounded-xl border border-border bg-card p-5', className)}>
      <div className="mb-4">
        <h3 className="text-sm font-semibold text-card-foreground">{title}</h3>
        {description && (
          <p className="text-xs text-muted-foreground mt-0.5">{description}</p>
        )}
      </div>
      {children}
    </div>
  );
}
