import { useEffect, useMemo } from 'react';
import { useNavigate, useParams } from 'react-router';
import { useQuery } from '@tanstack/react-query';
import { fetchSessionDetail } from '@/api/sessions';
import type { SessionRecord } from '@/api/types';
import {
  subscribeToConversationSnapshots,
  unsubscribeFromConversationSnapshots,
} from '@/realtime/useTelemetryStream';
import { useSessionSnapshots } from '@/stores/sessionSnapshotsStore';
import { PanelCard } from '@/components/panels/PanelCard';
import { LoadingSkeleton } from '@/components/panels/LoadingSkeleton';
import { EmptyState } from '@/components/panels/EmptyState';
import { ContextDrawer } from '@/components/context/ContextDrawer';
import { DEFAULT_CONTEXT_BUDGET } from '@/lib/categories';
import { StatusBadge } from './StatusBadge';
import { ToolsTable } from './ToolsTable';
import { SafetyTable } from './SafetyTable';
import {
  formatDuration,
  formatTokens,
  formatCost,
  formatTimestampFull,
  formatPercent,
} from './format';
import { SessionHero } from './gem/SessionHero';
import { SessionTimeline } from './gem/SessionTimeline';
import { useSessionGemState } from './gem/useSessionGemState';
import { useResolvedDrawerBody } from './gem/useResolvedDrawerBody';

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

  const conversationId = data?.session.conversationId;

  // Subscribe to live ContextSnapshot broadcasts for this conversation. The
  // realtime helpers track desired subscriptions independently of the SignalR
  // connection state — calling subscribe before the hub finishes connecting
  // queues the intent; calling unsubscribe drops it before any server-side
  // join lands. Either path keeps the page in hydrate-only mode safely.
  useEffect(() => {
    if (!conversationId) return;
    void subscribeToConversationSnapshots(conversationId);
    return () => {
      void unsubscribeFromConversationSnapshots(conversationId);
    };
  }, [conversationId]);

  const snapshots = useSessionSnapshots(conversationId);

  const gem = useSessionGemState({
    snapshots,
    fallbackBreakdown: data?.breakdown ?? null,
    messages: data?.messages ?? [],
    tools: data?.tools ?? [],
  });

  // Lazy-resolve the open drawer item's full body (messages → contentFull,
  // tools → card + args/stdout, registration categories → captured body) via the
  // shared hook. ContextInspectorPage uses the identical hook so both surfaces
  // resolve bodies the same way.
  const drawerBody = useResolvedDrawerBody(sessionId, gem.drawerItem);

  if (isLoading) {
    return (
      <div className="space-y-6">
        <BackButton onClick={() => navigate('/sessions')} />
        <LoadingSkeleton />
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

  const { session, tools, safetyEvents } = data;

  return (
    <div className="space-y-6">
      <BackButton onClick={() => navigate('/sessions')} />
      <SessionHeader session={session} />

      {session.errorMessage && <SessionVerdict message={session.errorMessage} />}

      <SessionHero
        snapshots={snapshots}
        gem={gem}
        budget={DEFAULT_CONTEXT_BUDGET}
        sessionId={sessionId}
      />

      <PanelCard
        title="Timeline"
        description={`${snapshots.length} turn${snapshots.length === 1 ? '' : 's'} captured`}
      >
        <SessionTimeline
          snapshots={snapshots}
          messages={data.messages}
          activeTurnIndex={gem.activeTurnIndex}
          activeCategory={gem.activeCategory}
          onTurnScrub={gem.setActiveTurnIndex}
          onLoadedClick={(item, turnIndex) =>
            gem.openDrawer(item, 'timeline', turnIndex)
          }
          budget={DEFAULT_CONTEXT_BUDGET}
        />
      </PanelCard>

      {tools.length > 0 && (
        <details data-testid="session-tools-panel" className="rounded-md border border-border bg-card">
          <summary className="cursor-pointer list-none px-4 py-2 text-xs font-medium uppercase tracking-wider text-muted-foreground hover:text-foreground">
            Tool executions · {tools.length}
          </summary>
          <div className="border-t border-border p-4">
            <ToolsTable tools={tools} />
          </div>
        </details>
      )}

      {safetyEvents.length > 0 && (
        <details data-testid="session-safety-panel" className="rounded-md border border-border bg-card">
          <summary className="cursor-pointer list-none px-4 py-2 text-xs font-medium uppercase tracking-wider text-muted-foreground hover:text-foreground">
            Safety events · {safetyEvents.length}
          </summary>
          <div className="border-t border-border p-4">
            <SafetyTable events={safetyEvents} />
          </div>
        </details>
      )}

      {/* The single page-level drawer; gem state owns the open/close lifecycle. */}
      <ContextDrawer
        open={gem.drawerItem !== null}
        onOpenChange={(open) => {
          if (!open) gem.closeDrawer();
        }}
        category={gem.drawerItem?.item.cat ?? 'system'}
        name={gem.drawerItem?.name ?? ''}
        path={gem.drawerItem?.path ?? ''}
        role={gem.drawerItem?.content.role}
        body={drawerBody}
        lang={gem.drawerItem?.content.lang}
        onPrev={() => gem.walkDrawer(-1)}
        onNext={() => gem.walkDrawer(1)}
      />
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

  const stats: { label: string; value: string; sub?: string }[] = useMemo(
    () => [
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
    ],
    [session, totalTokens],
  );

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
              <p className="text-[11px] uppercase tracking-wider text-muted-foreground">
                {s.label}
              </p>
              <p data-testid={`stat-${slug}-value`} className="text-base font-semibold font-mono tabular-nums text-card-foreground mt-0.5">
                {s.value}
              </p>
              {s.sub && (
                <p className="text-[10px] text-muted-foreground mt-0.5">{s.sub}</p>
              )}
            </div>
          );
        })}
      </div>
    </div>
  );
}

/* ------------------------------------------------------------------ */
/*  SessionVerdict — visible failure banner                            */
/* ------------------------------------------------------------------ */

/**
 * Renders `session.errorMessage` above the gem when the session ended in
 * error. The old TraceRail housed this in a right-rail Verdict box; the
 * pure-gem layout doesn't have a right rail, so the banner sits between the
 * stat strip and the hero — high enough to be the first thing an operator
 * triaging a failure sees.
 */
function SessionVerdict({ message }: { message: string }) {
  return (
    <div
      data-testid="session-verdict"
      role="alert"
      className="rounded-md border border-otel-negative/40 bg-otel-negative/10 px-4 py-3 text-sm text-otel-negative"
    >
      <p className="mb-1 text-[11px] font-semibold uppercase tracking-wider text-otel-negative/90">
        Verdict
      </p>
      <p className="break-words font-mono text-xs leading-relaxed">{message}</p>
    </div>
  );
}
