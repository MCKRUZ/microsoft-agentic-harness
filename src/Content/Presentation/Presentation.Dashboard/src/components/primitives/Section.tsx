interface SectionProps {
  title: string;
  subtitle?: string;
  kicker?: string;
  action?: React.ReactNode;
  children: React.ReactNode;
}

export function Section({ title, subtitle, kicker, action, children }: SectionProps) {
  const testId = `section-${title.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/(^-|-$)/g, '')}`;
  return (
    <div className="mb-6" data-testid={testId}>
      <div className="flex items-baseline justify-between mb-2.5">
        <div className="flex items-baseline gap-2.5">
          {kicker && (
            <span className="text-[10px] font-semibold text-otel-accent tracking-[0.18em] uppercase">
              {kicker}
            </span>
          )}
          <h3 className="text-sm text-foreground font-semibold m-0">
            {title}
            {subtitle && (
              <span className="font-normal text-otel-text-dim"> — {subtitle}</span>
            )}
          </h3>
        </div>
        {action && <div className="text-[11px] text-otel-text-dim">{action}</div>}
      </div>
      {children}
    </div>
  );
}
