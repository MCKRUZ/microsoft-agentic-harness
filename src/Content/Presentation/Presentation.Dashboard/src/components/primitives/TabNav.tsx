import { cn } from '@/lib/utils';

interface TabItem {
  label: string;
  description?: string;
}

interface TabNavProps {
  items: TabItem[];
  active: string;
  onChange: (label: string) => void;
}

export function TabNav({ items, active, onChange }: TabNavProps) {
  return (
    <div className="flex gap-0 border-b border-border mb-5">
      {items.map((tab) => (
        <button
          key={tab.label}
          onClick={() => onChange(tab.label)}
          className={cn(
            'px-4 py-2.5 -mb-px cursor-pointer bg-transparent border-b-2 transition-colors',
            tab.label === active
              ? 'border-otel-accent'
              : 'border-transparent hover:border-otel-text-mute/30',
          )}
        >
          <div className={cn(
            'text-[13px] font-semibold',
            tab.label === active ? 'text-foreground' : 'text-otel-text-dim',
          )}>
            {tab.label}
          </div>
          {tab.description && (
            <div className="text-[10px] text-otel-text-mute mt-0.5">{tab.description}</div>
          )}
        </button>
      ))}
    </div>
  );
}
