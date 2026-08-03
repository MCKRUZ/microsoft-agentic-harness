import { cn } from '@/lib/utils';

interface PanelGridProps {
  children: React.ReactNode;
  columns?: 1 | 2 | 3 | 4;
  className?: string;
}

// 1 is a real caller need (BudgetPage's full-width Alerts panel) and was being passed
// already. Without an entry here `colClasses[1]` was undefined, so the grid rendered with no
// column class at all and only looked correct because a bare `grid` falls back to one
// column. Stating it explicitly makes the layout intentional rather than incidental.
const colClasses = {
  1: 'grid-cols-1',
  2: 'grid-cols-1 md:grid-cols-2',
  3: 'grid-cols-1 md:grid-cols-2 lg:grid-cols-3',
  4: 'grid-cols-1 sm:grid-cols-2 lg:grid-cols-4',
};

export function PanelGrid({ children, columns = 3, className }: PanelGridProps) {
  return (
    <div className={cn('grid gap-4', colClasses[columns], className)}>
      {children}
    </div>
  );
}
