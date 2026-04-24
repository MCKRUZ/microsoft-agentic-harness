import { RefreshCw, Circle } from 'lucide-react';
import { TimeRangePicker } from './TimeRangePicker';
import { ThemeToggle } from '@/components/theme/ThemeToggle';
import { useTelemetryStore } from '@/stores/telemetryStore';
import { useTimeRangeStore } from '@/stores/timeRangeStore';
import { useQueryClient } from '@tanstack/react-query';
import { cn } from '@/lib/utils';

export function Topbar() {
  const connected = useTelemetryStore((s) => s.connected);
  const refreshInterval = useTimeRangeStore((s) => s.refreshIntervalSeconds);
  const queryClient = useQueryClient();

  return (
    <header className="h-14 border-b border-border bg-background flex items-center justify-between px-4">
      <div className="flex items-center gap-3">
        <TimeRangePicker />
        <div className="flex items-center gap-1.5 text-xs text-muted-foreground">
          <Circle
            className={cn('h-2 w-2 fill-current', connected ? 'text-green-500' : 'text-red-500')}
          />
          {connected ? 'Live' : 'Disconnected'}
        </div>
      </div>
      <div className="flex items-center gap-2">
        <span className="text-xs text-muted-foreground">{refreshInterval}s</span>
        <button
          onClick={() => queryClient.invalidateQueries()}
          className="p-2 rounded-md hover:bg-accent text-muted-foreground hover:text-foreground transition-colors"
          aria-label="Refresh all data"
        >
          <RefreshCw className="h-4 w-4" />
        </button>
        <ThemeToggle />
      </div>
    </header>
  );
}
