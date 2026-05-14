import { useQuery } from '@tanstack/react-query';
import { usePromQuery } from '@/hooks/usePromQuery';
import { KpiCard } from '@/components/panels/KpiCard';
import { PanelCard } from '@/components/panels/PanelCard';
import { PanelGrid } from '@/components/panels/PanelGrid';
import { LoadingSkeleton } from '@/components/panels/LoadingSkeleton';
import { TimeSeriesChart } from '@/components/charts/TimeSeriesChart';
import { HBarList } from '@/components/charts/HBarList';
import { StatusDot } from '@/components/primitives/StatusDot';
import { fetchSessions } from '@/api/sessions';
import {
  useMetric,
  latestValue,
  computeDelta,
  formatKpi,
  fmtRelative,
  seriesToBars,
} from './pulse-helpers';

const TURNS_PER_MIN =
  'rate(agentic_harness_agent_orchestration_turns_total[5m]) * 60 or vector(0)';
const TOOL_CALLS_PER_MIN =
  'rate(agentic_harness_agent_tool_invocations_total[5m]) * 60 or vector(0)';
const THROUGHPUT_BY_MODEL =
  'sum by (model) (rate(agentic_harness_agent_tokens_total_sum[5m]) * 60)';
const CONCURRENCY =
  'agentic_harness_agent_session_active or vector(0)';
const SESSIONS_BY_AGENT =
  'sum by (agent_name) (agentic_harness_agent_session_started_total)';
const TOOLS_BY_NAME =
  'sum by (agent_tool_name) (agentic_harness_agent_tool_invocations_total)';

export function ActivityTab() {
  const tokensPerMin = useMetric('tokens_per_minute');
  const activeSessions = useMetric('active_sessions');
  const turnsPerMin = usePromQuery(TURNS_PER_MIN);
  const toolCallsPerMin = usePromQuery(TOOL_CALLS_PER_MIN);
  const throughput = usePromQuery(THROUGHPUT_BY_MODEL);
  const concurrency = usePromQuery(CONCURRENCY);
  const sessionsByAgent = usePromQuery(SESSIONS_BY_AGENT);
  const toolsByName = usePromQuery(TOOLS_BY_NAME);
  const recentSessions = useQuery({
    queryKey: ['live-sessions-feed'],
    queryFn: () => fetchSessions(7),
    refetchInterval: 15_000,
  });

  const anyLoading =
    tokensPerMin.isLoading ||
    activeSessions.isLoading ||
    turnsPerMin.isLoading ||
    toolCallsPerMin.isLoading;

  if (anyLoading) {
    return (
      <div className="space-y-4">
        <PanelGrid columns={4}>
          {Array.from({ length: 4 }).map((_, i) => (
            <LoadingSkeleton key={i} />
          ))}
        </PanelGrid>
        <LoadingSkeleton className="h-48" />
      </div>
    );
  }

  const tokVal = latestValue(tokensPerMin.data);
  const activeVal = latestValue(activeSessions.data);
  const turnsVal = latestValue(turnsPerMin.data);
  const toolsVal = latestValue(toolCallsPerMin.data);

  const tokensDelta = computeDelta(tokensPerMin.data?.series[0]?.dataPoints);
  const sessionsDelta = computeDelta(activeSessions.data?.series[0]?.dataPoints);

  const agentBars = seriesToBars(
    sessionsByAgent.data?.series ?? [],
    'agent_name',
    (v) => v.toFixed(0),
  ).slice(0, 8);

  const toolBars = seriesToBars(
    toolsByName.data?.series ?? [],
    'agent_tool_name',
    (v) => v.toFixed(0),
  ).slice(0, 8);

  const feedItems = (recentSessions.data ?? []).map((s) => ({
    sessionId: s.id,
    agent: s.agentName,
    timestamp: new Date(s.startedAt).getTime(),
    active: s.status === 'active',
  }));

  return (
    <div className="space-y-6">
      <PanelGrid columns={4}>
        <KpiCard
          title="Tokens / min"
          description="Rate of LLM token consumption across all sessions over a 5-minute rolling window."
          value={formatKpi(tokVal, 'tokens/min')}
          unit="tok/min"
          delta={tokensDelta?.text}
          trend={tokensDelta?.trend}
          sparklineData={tokensPerMin.data?.series[0]?.dataPoints}
        />
        <KpiCard
          title="Active Sessions"
          description="Number of agent sessions currently open. Sessions stay active until explicitly ended or timed out."
          value={formatKpi(activeVal, 'count')}
          delta={sessionsDelta?.text}
          trend={sessionsDelta?.trend}
          sparklineData={activeSessions.data?.series[0]?.dataPoints}
        />
        <KpiCard
          title="Turns / min"
          description="Conversation turns per minute across all sessions. One turn = one user message and agent response cycle."
          value={turnsVal.toFixed(1)}
          sparklineData={turnsPerMin.data?.series[0]?.dataPoints}
        />
        <KpiCard
          title="Tool Calls / min"
          description="Rate of tool invocations across all sessions. Includes all registered tools (file system, search, calculation, etc.)."
          value={toolsVal.toFixed(1)}
          sparklineData={toolCallsPerMin.data?.series[0]?.dataPoints}
        />
      </PanelGrid>

      {/* Throughput + Live Feed */}
      <div className="grid grid-cols-1 lg:grid-cols-[2fr_1fr] gap-4">
        <PanelCard
          title="Throughput · last 60 min"
          description="tokens / min, by model"
        >
          <TimeSeriesChart
            series={throughput.data?.series ?? []}
            unit="tokens/min"
          />
        </PanelCard>

        <PanelCard
          title="Live session feed"
          description="most recent activity"
        >
          {feedItems.length === 0 ? (
            <p className="text-xs text-otel-text-mute py-8 text-center">
              No sessions yet
            </p>
          ) : (
            <div className="space-y-0">
              {feedItems.map((item, i) => (
                <div
                  key={i}
                  className="flex items-center gap-3 px-1 py-2 border-b border-border/30 last:border-0"
                >
                  <StatusDot
                    status={item.active ? 'active' : 'completed'}
                  />
                  <span className="text-xs font-mono text-otel-text-dim w-16 shrink-0 truncate">
                    {item.sessionId.slice(0, 9)}
                  </span>
                  <span className="text-xs text-card-foreground flex-1 truncate">
                    {item.agent}
                  </span>
                  <span className="text-[10px] text-otel-text-mute shrink-0">
                    {item.timestamp > 0 ? fmtRelative(item.timestamp) : '--'}
                  </span>
                </div>
              ))}
            </div>
          )}
        </PanelCard>
      </div>

      {/* Concurrency + By Agent */}
      <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
        <PanelCard
          title="Concurrency · 60 min"
          description="overlapping sessions"
        >
          <TimeSeriesChart
            series={concurrency.data?.series ?? []}
            unit="sessions"
          />
        </PanelCard>
        <PanelCard
          title="By agent"
          description="sessions per agent today"
        >
          <HBarList items={agentBars} />
        </PanelCard>
      </div>

      {/* Top Tools */}
      <PanelCard
        title="Top tools · last hour"
        description="call count per tool"
      >
        <HBarList items={toolBars} />
      </PanelCard>
    </div>
  );
}
