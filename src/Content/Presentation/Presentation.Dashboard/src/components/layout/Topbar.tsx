import { RefreshCw } from 'lucide-react';
import { TimeRangePicker } from './TimeRangePicker';
import { ThemeToggle } from '@/components/theme/ThemeToggle';
import { PulseDot } from '@/components/primitives/PulseDot';
import { useTelemetryStore } from '@/stores/telemetryStore';
import { useTimeRangeStore } from '@/stores/timeRangeStore';
import { useQueryClient } from '@tanstack/react-query';
import { useLocation } from 'react-router-dom';

const breadcrumbMap: Record<string, string> = {
  '/pulse': 'Pulse',
  '/sessions': 'Sessions',
  '/spend': 'Spend / Overview',
  '/spend/tokens': 'Spend / Tokens',
  '/spend/cost': 'Spend / Cost',
  '/spend/budget': 'Spend / Budget',
  '/quality': 'Quality / Overview',
  '/quality/tools': 'Quality / Tools',
  '/quality/safety': 'Quality / Safety',
  '/quality/rag': 'Quality / RAG',
  '/catalog': 'Catalog',
};

export function Topbar() {
  const connected = useTelemetryStore((s) => s.connected);
  const refreshInterval = useTimeRangeStore((s) => s.refreshIntervalSeconds);
  const queryClient = useQueryClient();
  const location = useLocation();

  const breadcrumb = breadcrumbMap[location.pathname] ?? location.pathname.split('/').filter(Boolean).join(' / ');

  return (
    <header className="h-12 border-b border-border bg-background flex items-center justify-between px-5">
      <div className="flex items-center gap-3">
        <span className="text-[11px] font-mono text-otel-text-dim">{breadcrumb}</span>
        <TimeRangePicker />
        <div className="flex items-center gap-1.5 ml-2">
          <PulseDot color={connected ? 'var(--otel-positive)' : 'var(--otel-negative)'} size={6} />
          <span className={`text-[10px] font-mono ${connected ? 'text-otel-positive' : 'text-otel-negative'}`}>
            {connected ? 'LIVE' : 'OFFLINE'}
          </span>
          <span className="text-[10px] text-otel-text-mute ml-1">· {refreshInterval}s</span>
        </div>
      </div>
      <div className="flex items-center gap-2">
        <button
          onClick={() => queryClient.invalidateQueries()}
          className="p-1.5 rounded-md hover:bg-accent text-muted-foreground hover:text-foreground transition-colors"
          aria-label="Refresh all data"
        >
          <RefreshCw className="h-3.5 w-3.5" />
        </button>
        <ThemeToggle />
      </div>
    </header>
  );
}
