import { useTimeRangeStore, type TimeRangePreset } from '@/stores/timeRangeStore';
import { cn } from '@/lib/utils';

const presets: { value: TimeRangePreset; label: string }[] = [
  { value: '1h', label: '1H' },
  { value: '6h', label: '6H' },
  { value: '24h', label: '24H' },
  { value: '7d', label: '7D' },
];

export function TimeRangePicker() {
  const { preset, setPreset } = useTimeRangeStore();

  return (
    <div className="flex items-center gap-1 bg-muted rounded-lg p-1">
      {presets.map((p) => (
        <button
          key={p.value}
          onClick={() => setPreset(p.value)}
          className={cn(
            'px-3 py-1 text-sm font-medium rounded-md transition-colors',
            preset === p.value
              ? 'bg-background text-foreground shadow-sm'
              : 'text-muted-foreground hover:text-foreground',
          )}
        >
          {p.label}
        </button>
      ))}
    </div>
  );
}
