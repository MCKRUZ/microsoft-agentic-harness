interface HBarItem {
  label: string;
  value: number;
}

interface HBarListProps {
  items: HBarItem[];
  max?: number;
  color?: string;
  formatValue?: (v: number) => string;
}

export function HBarList({ items, max, color = 'var(--otel-accent)', formatValue = (v) => String(v) }: HBarListProps) {
  const m = max ?? Math.max(...items.map((i) => i.value));

  return (
    <div className="flex flex-col gap-1.5">
      {items.map((item) => (
        <div key={item.label} className="flex items-center gap-2 text-[11px]">
          <div className="w-28 font-mono text-foreground whitespace-nowrap overflow-hidden text-ellipsis">
            {item.label}
          </div>
          <div className="flex-1 h-2.5 bg-background/10 rounded-sm relative">
            <div
              className="absolute left-0 top-0 bottom-0 rounded-sm"
              style={{ width: `${(item.value / m) * 100}%`, background: color }}
            />
          </div>
          <div className="w-14 text-right font-mono text-otel-text-dim tabular-nums">
            {formatValue(item.value)}
          </div>
        </div>
      ))}
    </div>
  );
}
