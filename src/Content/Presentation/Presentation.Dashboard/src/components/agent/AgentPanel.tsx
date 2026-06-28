import { useEffect, useRef, useState, type FormEvent } from 'react';
import * as Dialog from '@radix-ui/react-dialog';
import { Loader2, Send, Sparkles, X } from 'lucide-react';
import { cn } from '@/lib/utils';
import { useChatStore, type ChatMessage } from '@/stores/chatStore';
import { useDashboardAgent } from '@/hooks/useDashboardAgent';
import { TimeSeriesChart } from '@/components/charts/TimeSeriesChart';
import { MetricBarChart } from '@/components/charts/BarChart';

/**
 * Embedded dashboard agent panel — a right-side slide-over (Radix Dialog, mirroring
 * {@link ContextDrawer}) holding a chat transcript and input. The agent can both answer questions
 * and act on the dashboard (change the time range, navigate, refresh) via AG-UI tool calls handled
 * by {@link useDashboardAgent}. Mounted once in the shell so it is available on every page.
 */
export function AgentPanel() {
  const open = useChatStore((s) => s.open);
  const setOpen = useChatStore((s) => s.setOpen);
  const messages = useChatStore((s) => s.messages);
  const status = useChatStore((s) => s.status);
  const error = useChatStore((s) => s.error);
  const toolActivity = useChatStore((s) => s.toolActivity);
  const { sendMessage } = useDashboardAgent();

  const [draft, setDraft] = useState('');
  const scrollRef = useRef<HTMLDivElement>(null);

  // Keep the newest message in view as the transcript grows or streams.
  useEffect(() => {
    scrollRef.current?.scrollTo({ top: scrollRef.current.scrollHeight });
  }, [messages, toolActivity]);

  const running = status === 'running';

  const handleSubmit = (e: FormEvent) => {
    e.preventDefault();
    const text = draft.trim();
    if (!text || running) return;
    setDraft('');
    void sendMessage(text);
  };

  return (
    <Dialog.Root open={open} onOpenChange={setOpen}>
      <Dialog.Portal>
        <Dialog.Overlay
          data-testid="agent-panel-overlay"
          className="fixed inset-0 z-40 bg-foreground/30 backdrop-blur-sm data-[state=open]:animate-in data-[state=closed]:animate-out data-[state=open]:fade-in-0 data-[state=closed]:fade-out-0"
        />
        <Dialog.Content
          data-testid="agent-panel"
          className="fixed right-0 top-0 z-50 h-full w-full max-w-md bg-card border-l border-border shadow-2xl flex flex-col focus:outline-none data-[state=open]:animate-in data-[state=closed]:animate-out data-[state=open]:slide-in-from-right data-[state=closed]:slide-out-to-right"
        >
          <header className="flex items-center gap-2 border-b border-border bg-card px-5 py-4">
            <Sparkles className="h-4 w-4 text-cat-accent" />
            <div className="flex-1 min-w-0">
              <Dialog.Title className="text-sm font-semibold text-foreground">Dashboard Agent</Dialog.Title>
              <Dialog.Description className="text-xs text-muted-foreground">
                Ask about the dashboard or tell it what to show.
              </Dialog.Description>
            </div>
            <Dialog.Close
              data-testid="agent-panel-close"
              className="rounded-md p-1 text-muted-foreground hover:bg-accent hover:text-foreground"
              aria-label="Close agent panel"
            >
              <X className="h-4 w-4" />
            </Dialog.Close>
          </header>

          <div ref={scrollRef} data-testid="agent-panel-transcript" className="flex-1 overflow-auto px-5 py-4 space-y-3">
            {messages.length === 0 && !running && (
              <p className="text-xs text-muted-foreground">
                Try: “show me the last 24 hours on the spend page”, “what am I looking at?”, or “refresh the data”.
              </p>
            )}
            {messages.map((m) => (
              <MessageBubble key={m.id} message={m} />
            ))}
            {toolActivity && (
              <div data-testid="agent-panel-activity" className="flex items-center gap-2 text-xs text-muted-foreground">
                <Loader2 className="h-3 w-3 animate-spin" />
                <span>{toolActivity}…</span>
              </div>
            )}
            {error && (
              <div data-testid="agent-panel-error" className="rounded-md border border-otel-negative/40 bg-otel-negative/10 px-3 py-2 text-xs text-otel-negative">
                {error}
              </div>
            )}
          </div>

          <form onSubmit={handleSubmit} className="border-t border-border bg-card px-3 py-3">
            <div className="flex items-end gap-2">
              <input
                data-testid="agent-panel-input"
                value={draft}
                onChange={(e) => setDraft(e.target.value)}
                placeholder="Message the dashboard agent…"
                aria-label="Message the dashboard agent"
                className="flex-1 rounded-md border border-border bg-background px-3 py-2 text-sm text-foreground placeholder:text-muted-foreground focus:outline-none focus:ring-1 focus:ring-ring"
              />
              <button
                type="submit"
                data-testid="agent-panel-send"
                disabled={running || draft.trim().length === 0}
                aria-label="Send message"
                className="flex h-9 w-9 items-center justify-center rounded-md bg-primary text-primary-foreground hover:opacity-90 disabled:opacity-40"
              >
                {running ? <Loader2 className="h-4 w-4 animate-spin" /> : <Send className="h-4 w-4" />}
              </button>
            </div>
          </form>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}

function MessageBubble({ message }: { message: ChatMessage }) {
  if (message.chart) return <ChartCard message={message} />;

  const isUser = message.role === 'user';
  return (
    <div data-testid={`agent-message-${message.role}`} className={cn('flex', isUser ? 'justify-end' : 'justify-start')}>
      <div
        className={cn(
          'max-w-[85%] rounded-lg px-3 py-2 text-sm whitespace-pre-wrap break-words',
          isUser ? 'bg-primary text-primary-foreground' : 'bg-muted text-foreground',
        )}
      >
        {message.content || <span className="text-muted-foreground">…</span>}
      </div>
    </div>
  );
}

/**
 * Renders a chart the agent generated inline, reusing the dashboard's existing chart components
 * (bar/pie → {@link MetricBarChart}, otherwise → {@link TimeSeriesChart}) populated from real metric data.
 */
function ChartCard({ message }: { message: ChatMessage }) {
  const chart = message.chart!;
  const isBar = chart.chartType === 'bar' || chart.chartType === 'pie';
  return (
    <div data-testid="agent-message-chart" className="rounded-lg border border-border bg-muted/40 p-3">
      <div className="mb-2 text-xs font-medium text-foreground">{chart.title}</div>
      <div className="h-48">
        {chart.series.length === 0 ? (
          <p className="text-xs text-muted-foreground">No data for the current time range.</p>
        ) : isBar ? (
          <MetricBarChart series={chart.series} unit={chart.unit} />
        ) : (
          <TimeSeriesChart series={chart.series} unit={chart.unit} />
        )}
      </div>
      {message.content && <p className="mt-2 text-xs text-muted-foreground">{message.content}</p>}
    </div>
  );
}
