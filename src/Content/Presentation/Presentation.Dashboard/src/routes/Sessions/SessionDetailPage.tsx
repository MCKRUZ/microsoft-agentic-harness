import { useNavigate, useParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { fetchSessionDetail } from '@/api/sessions';
import type { SessionRecord } from '@/api/types';
import { KpiCard } from '@/components/panels/KpiCard';
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
          {Array.from({ length: 4 }).map((_, i) => <LoadingSkeleton key={i} />)}
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
  const totalTokens = session.totalInputTokens + session.totalOutputTokens;

  return (
    <div className="space-y-6">
      <BackButton onClick={() => navigate('/sessions')} />

      <SessionHeader session={session} />

      <PanelGrid columns={4}>
        <KpiCard title="Total Turns" value={session.turnCount.toString()} />
        <KpiCard
          title="Total Tokens"
          value={formatTokens(totalTokens)}
          unit={`${formatTokens(session.totalInputTokens)} in / ${formatTokens(session.totalOutputTokens)} out`}
        />
        <KpiCard title="Cache Hit Rate" value={formatPercent(session.cacheHitRate)} />
        <KpiCard title="Total Cost" value={formatCost(session.totalCostUsd)} />
      </PanelGrid>

      <PanelGrid columns={2}>
        <KpiCard title="Tool Calls" value={session.toolCallCount.toString()} />
        <KpiCard title="Subagents" value={session.subagentCount.toString()} />
      </PanelGrid>

      <PanelCard title="Conversation Timeline" description={`${messages.length} messages across ${session.turnCount} turns`}>
        {messages.length > 0 ? (
          <ConversationTimeline messages={messages} toolExecutions={tools} />
        ) : (
          <EmptyState title="No messages recorded" />
        )}
      </PanelCard>

      {tools.length > 0 && (
        <PanelCard title="Tool Executions" description={`${tools.length} tool calls`}>
          <ToolsTable tools={tools} />
        </PanelCard>
      )}

      {safetyEvents.length > 0 && (
        <PanelCard title="Safety Events" description={`${safetyEvents.length} events`}>
          <SafetyTable events={safetyEvents} />
        </PanelCard>
      )}
    </div>
  );
}

function BackButton({ onClick }: { onClick: () => void }) {
  return (
    <button
      onClick={onClick}
      className="flex items-center gap-1.5 text-sm text-muted-foreground hover:text-foreground transition-colors"
    >
      <svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
        <path d="m15 18-6-6 6-6" />
      </svg>
      Back to Sessions
    </button>
  );
}

function SessionHeader({ session }: { session: SessionRecord }) {
  return (
    <div className="rounded-xl border border-border bg-card p-5">
      <div className="flex items-start justify-between gap-4">
        <div className="space-y-1">
          <div className="flex items-center gap-3">
            <h2 className="text-lg font-bold text-card-foreground">{session.agentName}</h2>
            <StatusBadge status={session.status} />
          </div>
          <p className="text-xs font-mono text-muted-foreground">{session.id}</p>
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
      {session.errorMessage && (
        <div className="mt-3 p-3 rounded-lg bg-red-500/10 border border-red-500/20 text-sm text-red-400">
          {session.errorMessage}
        </div>
      )}
    </div>
  );
}
