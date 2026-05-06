import { useMemo } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { fetchSessionDetail } from '@/api/sessions';
import type {
  SessionRecord,
  SessionMessageRecord,
  ToolExecutionRecord,
} from '@/api/types';
import { PanelCard } from '@/components/panels/PanelCard';
import { PanelGrid } from '@/components/panels/PanelGrid';
import { LoadingSkeleton } from '@/components/panels/LoadingSkeleton';
import { EmptyState } from '@/components/panels/EmptyState';
import { StatusBadge } from './StatusBadge';
import { ConversationTimeline } from './ConversationTimeline';
import { ToolsTable } from './ToolsTable';
import { SafetyTable } from './SafetyTable';
import {
  formatDuration,
  formatTokens,
  formatCost,
  formatTimestampFull,
  formatPercent,
} from './format';

/* ------------------------------------------------------------------ */
/*  Main page                                                         */
/* ------------------------------------------------------------------ */

export default function SessionDetailPage() {
  const { sessionId } = useParams<{ sessionId: string }>();
  const navigate = useNavigate();

  const { data, isLoading, isError } = useQuery({
    queryKey: ['session-detail', sessionId],
    queryFn: () => fetchSessionDetail(sessionId!),
    enabled: !!sessionId,
  });

  if (isLoading) {
    return (
      <div className="space-y-6">
        <BackButton onClick={() => navigate('/sessions')} />
        <PanelGrid columns={4}>
          {Array.from({ length: 4 }).map((_, i) => (
            <LoadingSkeleton key={i} />
          ))}
        </PanelGrid>
        <LoadingSkeleton />
      </div>
    );
  }

  if (isError || !data) {
    return (
      <div className="space-y-6">
        <BackButton onClick={() => navigate('/sessions')} />
        <PanelCard title="Session Not Found">
          <EmptyState
            title="Unable to load session"
            description="This session may not exist or the session store is unavailable."
          />
        </PanelCard>
      </div>
    );
  }

  const { session, messages, tools, safetyEvents } = data;

  return (
    <div className="space-y-6">
      <BackButton onClick={() => navigate('/sessions')} />
      <SessionHeader session={session} />

      {/* Two-column body */}
      <div className="flex gap-6 items-start">
        {/* Left column */}
        <div className="flex-1 min-w-0 space-y-6">
          <CostWaterfall messages={messages} />

          <PanelCard
            title="Conversation Timeline"
            description={`${messages.length} messages across ${session.turnCount} turns`}
          >
            {messages.length > 0 ? (
              <ConversationTimeline
                messages={messages}
                toolExecutions={tools}
              />
            ) : (
              <EmptyState title="No messages recorded" />
            )}
          </PanelCard>

          {tools.length > 0 && (
            <PanelCard
              title="Tool Executions"
              description={`${tools.length} tool calls`}
            >
              <ToolsTable tools={tools} />
            </PanelCard>
          )}

          {safetyEvents.length > 0 && (
            <PanelCard
              title="Safety Events"
              description={`${safetyEvents.length} events`}
            >
              <SafetyTable events={safetyEvents} />
            </PanelCard>
          )}
        </div>

        {/* Right column */}
        <TraceRail session={session} messages={messages} tools={tools} />
      </div>
    </div>
  );
}

/* ------------------------------------------------------------------ */
/*  BackButton                                                        */
/* ------------------------------------------------------------------ */

function BackButton({ onClick }: { onClick: () => void }) {
  return (
    <button
      onClick={onClick}
      className="flex items-center gap-1.5 text-sm text-otel-accent hover:text-otel-accent-dim transition-colors"
    >
      <svg
        xmlns="http://www.w3.org/2000/svg"
        width="16"
        height="16"
        viewBox="0 0 24 24"
        fill="none"
        stroke="currentColor"
        strokeWidth="2"
        strokeLinecap="round"
        strokeLinejoin="round"
      >
        <path d="m15 18-6-6 6-6" />
      </svg>
      ← sessions
    </button>
  );
}

/* ------------------------------------------------------------------ */
/*  SessionHeader with stat strip                                     */
/* ------------------------------------------------------------------ */

function SessionHeader({ session }: { session: SessionRecord }) {
  const totalTokens = session.totalInputTokens + session.totalOutputTokens;

  const stats: { label: string; value: string; sub?: string }[] = [
    { label: 'Turns', value: session.turnCount.toString() },
    { label: 'Duration', value: formatDuration(session.durationMs) },
    {
      label: 'Tokens',
      value: formatTokens(totalTokens),
      sub: `${formatTokens(session.totalInputTokens)} in / ${formatTokens(session.totalOutputTokens)} out`,
    },
    { label: 'Cache Hit', value: formatPercent(session.cacheHitRate) },
    { label: 'Tool Calls', value: session.toolCallCount.toString() },
    { label: 'Cost', value: formatCost(session.totalCostUsd) },
    { label: 'Subagents', value: session.subagentCount.toString() },
  ];

  return (
    <div className="space-y-3">
      {/* Identity row */}
      <div data-testid="session-identity" className="rounded-xl border border-border bg-card p-5">
        <div className="flex items-start justify-between gap-4">
          <div className="space-y-1">
            <div className="flex items-center gap-3">
              <h2 className="text-lg font-bold text-card-foreground">
                {session.agentName}
              </h2>
              <StatusBadge status={session.status} />
            </div>
            <p className="text-xs font-mono text-muted-foreground">
              {session.id}
            </p>
            {session.model && (
              <p className="text-sm text-muted-foreground">{session.model}</p>
            )}
          </div>
          <div className="text-right space-y-1 text-sm text-muted-foreground">
            <p>Started: {formatTimestampFull(session.startedAt)}</p>
            <p>Ended: {formatTimestampFull(session.endedAt)}</p>
            <p>Duration: {formatDuration(session.durationMs)}</p>
          </div>
        </div>
      </div>

      {/* Stat strip */}
      <div data-testid="stat-strip" className="bg-card border border-border rounded-md grid grid-cols-7">
        {stats.map((s, i) => {
          const slug = s.label.toLowerCase().replace(/\s+/g, '-');
          return (
          <div
            key={s.label}
            data-testid={`stat-${slug}`}
            className={`px-4 py-3 text-center ${i < stats.length - 1 ? 'border-r border-border' : ''}`}
          >
            <p className="text-[11px] uppercase tracking-wider text-otel-text-mute">
              {s.label}
            </p>
            <p data-testid={`stat-${slug}-value`} className="text-base font-semibold font-mono tabular-nums text-card-foreground mt-0.5">
              {s.value}
            </p>
            {s.sub && (
              <p className="text-[10px] text-otel-text-dim mt-0.5">{s.sub}</p>
            )}
          </div>
          );
        })}
      </div>
    </div>
  );
}

/* ------------------------------------------------------------------ */
/*  CostWaterfall                                                     */
/* ------------------------------------------------------------------ */

interface TurnCost {
  turn: number;
  inputTokens: number;
  outputTokens: number;
  cost: number;
  cumulative: number;
}

function CostWaterfall({ messages }: { messages: SessionMessageRecord[] }) {
  const turns = useMemo(() => {
    const grouped = new Map<number, { input: number; output: number; cost: number }>();
    for (const m of messages) {
      const existing = grouped.get(m.turnIndex) ?? { input: 0, output: 0, cost: 0 };
      existing.input += m.inputTokens;
      existing.output += m.outputTokens;
      existing.cost += m.costUsd;
      grouped.set(m.turnIndex, existing);
    }

    let cumulative = 0;
    const result: TurnCost[] = [];
    const sortedKeys = [...grouped.keys()].sort((a, b) => a - b);
    for (const turn of sortedKeys) {
      const g = grouped.get(turn)!;
      cumulative += g.cost;
      result.push({
        turn,
        inputTokens: g.input,
        outputTokens: g.output,
        cost: g.cost,
        cumulative,
      });
    }
    return result;
  }, [messages]);

  const maxCost = useMemo(
    () => Math.max(...turns.map((t) => t.cost), 0.0001),
    [turns],
  );

  if (turns.length === 0) return null;

  return (
    <PanelCard
      title="Cost Waterfall"
      description="Cost per conversation turn"
    >
      <div className="space-y-1" data-testid="cost-waterfall-rows">
        {/* Header */}
        <div className="grid grid-cols-[3rem_1fr_6rem_5rem_5rem_5.5rem] gap-2 text-[11px] uppercase tracking-wider text-otel-text-mute px-1 pb-1 border-b border-border">
          <span>Turn</span>
          <span />
          <span className="text-right">Tokens In</span>
          <span className="text-right">Out</span>
          <span className="text-right">Cost</span>
          <span className="text-right">Cumul.</span>
        </div>

        {turns.map((t) => {
          const pct = (t.cost / maxCost) * 100;
          return (
            <div
              key={t.turn}
              data-testid={`cost-row-${t.turn}`}
              className="grid grid-cols-[3rem_1fr_6rem_5rem_5rem_5.5rem] gap-2 items-center px-1 py-0.5 rounded hover:bg-muted/40 transition-colors"
            >
              <span className="font-mono tabular-nums text-xs text-otel-text-dim">
                #{t.turn}
              </span>
              <div className="h-4 rounded-sm overflow-hidden bg-muted/30">
                <div
                  className="h-full rounded-sm bg-otel-accent/70"
                  style={{ width: `${pct}%` }}
                />
              </div>
              <span className="text-right font-mono tabular-nums text-xs text-otel-text-dim">
                {formatTokens(t.inputTokens)}
              </span>
              <span className="text-right font-mono tabular-nums text-xs text-otel-text-dim">
                {formatTokens(t.outputTokens)}
              </span>
              <span className="text-right font-mono tabular-nums text-xs text-foreground">
                {formatCost(t.cost)}
              </span>
              <span className="text-right font-mono tabular-nums text-xs text-otel-text-mute">
                {formatCost(t.cumulative)}
              </span>
            </div>
          );
        })}
      </div>
    </PanelCard>
  );
}

/* ------------------------------------------------------------------ */
/*  TraceRail sidebar                                                 */
/* ------------------------------------------------------------------ */

interface ToolAggregate {
  name: string;
  count: number;
  avgDurationMs: number | null;
  hasError: boolean;
}

function TraceRail({
  session,
  messages,
  tools,
}: {
  session: SessionRecord;
  messages: SessionMessageRecord[];
  tools: ToolExecutionRecord[];
}) {
  const totalTokens =
    session.totalInputTokens +
    session.totalCacheRead +
    session.totalOutputTokens;

  const segments = useMemo(() => {
    if (totalTokens === 0) return { input: 0, cache: 0, output: 0 };
    return {
      input: (session.totalInputTokens / totalTokens) * 100,
      cache: (session.totalCacheRead / totalTokens) * 100,
      output: (session.totalOutputTokens / totalTokens) * 100,
    };
  }, [session, totalTokens]);

  const toolAggregates = useMemo(() => {
    const map = new Map<string, { count: number; totalMs: number; durationCount: number; hasError: boolean }>();
    for (const t of tools) {
      const existing = map.get(t.toolName) ?? {
        count: 0,
        totalMs: 0,
        durationCount: 0,
        hasError: false,
      };
      existing.count++;
      if (t.durationMs !== null) {
        existing.totalMs += t.durationMs;
        existing.durationCount++;
      }
      if (t.status !== 'ok' && t.status !== 'success') {
        existing.hasError = true;
      }
      map.set(t.toolName, existing);
    }

    const result: ToolAggregate[] = [];
    for (const [name, agg] of map) {
      result.push({
        name,
        count: agg.count,
        avgDurationMs: agg.durationCount > 0 ? agg.totalMs / agg.durationCount : null,
        hasError: agg.hasError,
      });
    }
    return result.sort((a, b) => b.count - a.count);
  }, [tools]);

  return (
    <aside className="w-72 shrink-0 space-y-4 sticky top-6">
      {/* Token Shape */}
      <div className="bg-card border border-border rounded-md p-4 space-y-3">
        <h3 className="text-xs font-semibold uppercase tracking-wider text-otel-text-mute">
          Token Shape
        </h3>

        {/* Stacked bar */}
        <div className="h-5 rounded-sm overflow-hidden flex bg-muted/30">
          {segments.input > 0 && (
            <div
              className="h-full bg-otel-info"
              style={{ width: `${segments.input}%` }}
              title={`Input: ${formatTokens(session.totalInputTokens)}`}
            />
          )}
          {segments.cache > 0 && (
            <div
              className="h-full bg-otel-positive"
              style={{ width: `${segments.cache}%` }}
              title={`Cache read: ${formatTokens(session.totalCacheRead)}`}
            />
          )}
          {segments.output > 0 && (
            <div
              className="h-full bg-otel-accent"
              style={{ width: `${segments.output}%` }}
              title={`Output: ${formatTokens(session.totalOutputTokens)}`}
            />
          )}
        </div>

        {/* Legend */}
        <div className="space-y-1.5 text-xs">
          <LegendRow
            color="bg-otel-info"
            label="Input"
            value={formatTokens(session.totalInputTokens)}
          />
          <LegendRow
            color="bg-otel-positive"
            label="Cache read"
            value={formatTokens(session.totalCacheRead)}
          />
          <LegendRow
            color="bg-otel-accent"
            label="Output"
            value={formatTokens(session.totalOutputTokens)}
          />
        </div>
      </div>

      {/* Tools Used */}
      {toolAggregates.length > 0 && (
        <div className="bg-card border border-border rounded-md p-4 space-y-3">
          <h3 className="text-xs font-semibold uppercase tracking-wider text-otel-text-mute">
            Tools Used
          </h3>
          <div className="space-y-2">
            {toolAggregates.map((t) => (
              <div key={t.name} className="flex items-start gap-2">
                <span
                  className={`mt-1 inline-block w-2 h-2 rounded-full shrink-0 ${t.hasError ? 'bg-otel-negative' : 'bg-otel-positive'}`}
                />
                <div className="min-w-0 flex-1">
                  <p className="text-xs font-medium text-foreground truncate">
                    {t.name}
                  </p>
                  <p className="text-[11px] text-otel-text-dim font-mono tabular-nums">
                    {t.count}x
                    {t.avgDurationMs !== null && (
                      <> &middot; avg {formatDuration(t.avgDurationMs)}</>
                    )}
                  </p>
                </div>
              </div>
            ))}
          </div>
        </div>
      )}

      {/* Verdict */}
      {session.errorMessage && (
        <div className="bg-otel-negative/10 border border-otel-negative/20 rounded-md p-4 space-y-1">
          <h3 className="text-xs font-semibold uppercase tracking-wider text-otel-negative">
            Verdict
          </h3>
          <p className="text-sm text-otel-negative/90 break-words">
            {session.errorMessage}
          </p>
        </div>
      )}
    </aside>
  );
}

/* ------------------------------------------------------------------ */
/*  Legend helper                                                      */
/* ------------------------------------------------------------------ */

function LegendRow({
  color,
  label,
  value,
}: {
  color: string;
  label: string;
  value: string;
}) {
  return (
    <div className="flex items-center justify-between">
      <div className="flex items-center gap-1.5">
        <span className={`inline-block w-2.5 h-2.5 rounded-sm ${color}`} />
        <span className="text-otel-text-dim">{label}</span>
      </div>
      <span className="font-mono tabular-nums text-foreground">{value}</span>
    </div>
  );
}
