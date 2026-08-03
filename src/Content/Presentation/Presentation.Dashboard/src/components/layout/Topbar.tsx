import { RefreshCw, Sparkles } from 'lucide-react';
import { TimeRangePicker } from './TimeRangePicker';
import { ThemeToggle } from '@/components/theme/ThemeToggle';
import { useTelemetryStore } from '@/stores/telemetryStore';
import { useTimeRangeStore } from '@/stores/timeRangeStore';
import { useChatStore } from '@/stores/chatStore';
import { useQueryClient } from '@tanstack/react-query';
import { useLocation } from 'react-router';

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

/**
 * Animated status pip — pulses outward when connected to convey live data
 * flow. Inlined here (instead of a `PulseDot` primitive) because Topbar is
 * the only consumer; relies on the `pulse-ring` keyframe in index.css.
 */
function ConnectionPip({ connected }: { connected: boolean }) {
  const color = connected ? 'var(--otel-positive)' : 'var(--otel-negative)';
  return (
    <span className="relative inline-block" style={{ width: 6, height: 6 }}>
      {connected && (
        <span
          className="absolute inset-0 rounded-full opacity-40"
          style={{ background: color, animation: 'pulse-ring 1.6s ease-out infinite' }}
        />
      )}
      <span className="absolute inset-0 rounded-full" style={{ background: color }} />
    </span>
  );
}

export function Topbar() {
  const connected = useTelemetryStore((s) => s.connected);
  const refreshInterval = useTimeRangeStore((s) => s.refreshIntervalSeconds);
  const toggleAgent = useChatStore((s) => s.toggle);
  const queryClient = useQueryClient();
  const location = useLocation();

  const breadcrumb = breadcrumbMap[location.pathname] ?? location.pathname.split('/').filter(Boolean).join(' / ');

  return (
    <header className="h-12 border-b border-border bg-background flex items-center justify-between px-5">
      <div className="flex items-center gap-3">
        <span className="text-[11px] font-mono text-muted-foreground">{breadcrumb}</span>
        <TimeRangePicker />
        <div className="flex items-center gap-1.5 ml-2">
          <ConnectionPip connected={connected} />
          <span className={`text-[10px] font-mono ${connected ? 'text-otel-positive' : 'text-otel-negative'}`}>
            {connected ? 'LIVE' : 'OFFLINE'}
          </span>
          <span className="text-[10px] text-muted-foreground ml-1">· {refreshInterval}s</span>
        </div>
      </div>
      <div className="flex items-center gap-2">
        <button
          onClick={toggleAgent}
          className="p-1.5 rounded-md hover:bg-accent text-muted-foreground hover:text-foreground transition-colors"
          aria-label="Open dashboard agent"
          data-testid="agent-launcher"
        >
          <Sparkles className="h-3.5 w-3.5" />
        </button>
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
