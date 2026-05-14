interface PageHeaderProps {
  title: string;
  subtitle?: string;
  action?: React.ReactNode;
}

export function PageHeader({ title, subtitle, action }: PageHeaderProps) {
  return (
    <div className="flex items-baseline justify-between mb-3.5">
      <div>
        <h1 className="text-[22px] font-bold text-foreground tracking-[-0.01em] m-0">
          {title}
        </h1>
        {subtitle && (
          <p className="text-xs text-otel-text-dim mt-1">{subtitle}</p>
        )}
      </div>
      {action}
    </div>
  );
}
