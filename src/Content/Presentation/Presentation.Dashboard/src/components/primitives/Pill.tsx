import { cn } from '@/lib/utils';

interface PillProps {
  children: React.ReactNode;
  variant?: 'default' | 'positive' | 'negative' | 'warning' | 'info';
  className?: string;
}

const variantClasses: Record<string, string> = {
  default: 'text-otel-accent bg-otel-accent/10',
  positive: 'text-otel-positive bg-otel-positive/10',
  negative: 'text-otel-negative bg-otel-negative/10',
  warning: 'text-otel-warning bg-otel-warning/10',
  info: 'text-otel-info bg-otel-info/10',
};

export function Pill({ children, variant = 'default', className }: PillProps) {
  return (
    <span
      className={cn(
        'inline-block text-[10px] font-mono tracking-[0.04em] px-1.5 py-0.5 rounded-sm',
        variantClasses[variant],
        className,
      )}
    >
      {children}
    </span>
  );
}
