import { usePromQuery } from '@/hooks/usePromQuery';
import { KpiCard } from '@/components/panels/KpiCard';
import { PanelCard } from '@/components/panels/PanelCard';
import { PanelGrid } from '@/components/panels/PanelGrid';
import { LoadingSkeleton } from '@/components/panels/LoadingSkeleton';
import { Section } from '@/components/primitives/Section';
import { SloBoard } from './SloBoard';
import { CheckCircle, AlertTriangle, Info } from 'lucide-react';
import { useMetric, latestValue, computeDelta, formatKpi, primarySeries } from './pulse-helpers';
import { costRateQuery } from '@/config/costQueries';
import { cn } from '@/lib/utils';

const AVG_TURN_QUERY =
  'agentic_harness_agent_orchestration_turn_duration_sum / agentic_harness_agent_orchestration_turn_duration_count or vector(0)';
const ERROR_RATE_QUERY =
  'sum(rate(agentic_harness_agent_tool_errors_total[5m])) / (sum(rate(agentic_harness_agent_tool_invocations_total[5m])) > 0) or vector(0)';
const SAFETY_BLOCKS_QUERY =
  'sum(agentic_harness_agent_safety_blocks_total) or vector(0)';
const COST_RATE_QUERY = costRateQuery;

export function HealthTab() {
  const tokensPerMin = useMetric('tokens_per_minute');
  const activeSessions = useMetric('active_sessions');
  const avgTurn = usePromQuery(AVG_TURN_QUERY);
  const errorRate = usePromQuery(ERROR_RATE_QUERY);
  const costRate = usePromQuery(COST_RATE_QUERY);
  const safetyBlocks = usePromQuery(SAFETY_BLOCKS_QUERY);

  const anyLoading =
    tokensPerMin.isLoading ||
    activeSessions.isLoading ||
    avgTurn.isLoading ||
    errorRate.isLoading;

  if (anyLoading) {
    return (
      <div className="space-y-4">
        <LoadingSkeleton className="h-32" />
        <PanelGrid columns={3}>
          {Array.from({ length: 6 }).map((_, i) => (
            <LoadingSkeleton key={i} />
          ))}
        </PanelGrid>
      </div>
    );
  }

  const errorRateVal = Math.min(latestValue(errorRate.data), 1);
  const avgTurnVal = latestValue(avgTurn.data);
  const safetyBlocksVal = latestValue(safetyBlocks.data);
  const activeVal = latestValue(activeSessions.data);
  const tokVal = latestValue(tokensPerMin.data);
  const costRateVal = latestValue(costRate.data);
  const isHealthy = errorRateVal < 0.05 && safetyBlocksVal < 10;

  const tokensDelta = computeDelta(primarySeries(tokensPerMin.data)?.dataPoints);
  const sessionsDelta = computeDelta(primarySeries(activeSessions.data)?.dataPoints);

  return (
    <div className="space-y-6">
      {/* Verdict Banner */}
      <div className="rounded-xl border border-border bg-card p-5 flex items-start justify-between gap-6">
        <div className="flex items-start gap-3 flex-1 min-w-0">
          <div
            className={cn(
              'flex items-center justify-center w-10 h-10 rounded-full shrink-0 mt-0.5',
              isHealthy ? 'bg-emerald-500/15' : 'bg-red-500/15',
            )}
          >
            {isHealthy ? (
              <CheckCircle className="w-5 h-5 text-emerald-500" />
            ) : (
              <AlertTriangle className="w-5 h-5 text-red-500" />
            )}
          </div>
          <div>
            <h2 className="text-base font-semibold text-card-foreground m-0">
              System {isHealthy ? 'healthy' : 'degraded'}
            </h2>
            <p className="text-xs text-muted-foreground mt-1 leading-relaxed max-w-xl">
              {activeVal > 0
                ? `${activeVal} session${activeVal !== 1 ? 's' : ''} running. Throughput at ${formatKpi(tokVal, 'tokens/min')} tok/min. `
                : 'No active sessions. '}
              {errorRateVal > 0
                ? `Error rate ${formatKpi(errorRateVal, 'percent')}. `
                : 'No errors detected. '}
              {avgTurnVal > 0
                ? `Avg turn latency ${avgTurnVal >= 1 ? formatKpi(avgTurnVal, 's') : formatKpi(avgTurnVal * 1000, 'ms')}.`
                : ''}
            </p>
          </div>
        </div>
        <div className="flex items-center gap-3 shrink-0">
          <StatBadge
            label="Error Rate"
            description="Tool error ratio over 5 min. Below 5% is healthy."
            value={formatKpi(errorRateVal, 'percent')}
            variant={
              errorRateVal < 0.05
                ? 'default'
                : errorRateVal < 0.1
                  ? 'warning'
                  : 'critical'
            }
          />
          <StatBadge
            label="P50 Turn"
            description="Median time for one agent turn including LLM and tools."
            value={
              avgTurnVal >= 1
                ? formatKpi(avgTurnVal, 's')
                : formatKpi(avgTurnVal * 1000, 'ms')
            }
            variant="default"
          />
          <StatBadge
            label="Safety Blocks"
            description="Requests blocked by content safety. Below 10 is healthy."
            value={safetyBlocksVal.toFixed(0)}
            variant={safetyBlocksVal > 5 ? 'warning' : 'default'}
          />
        </div>
      </div>

      {/* Sparkline Strip */}
      <Section title="At a glance" kicker="01">
        <PanelGrid columns={3}>
          <KpiCard
            title="Tokens / min"
            description="Rate of LLM token consumption across all sessions over a 5-minute rolling window. Shows 0 when no sessions are actively streaming."
            value={formatKpi(tokVal, 'tokens/min')}
            unit="tok/min"
            delta={tokensDelta?.text}
            trend={tokensDelta?.trend}
            sparklineData={primarySeries(tokensPerMin.data)?.dataPoints}
          />
          <KpiCard
            title="Active Sessions"
            description="Number of agent sessions currently open. A session stays active until explicitly ended or timed out."
            value={formatKpi(activeVal, 'count')}
            delta={sessionsDelta?.text}
            trend={sessionsDelta?.trend}
            sparklineData={primarySeries(activeSessions.data)?.dataPoints}
          />
          <KpiCard
            title="Avg Turn"
            description="Average wall-clock time for one agent turn (user message in, assistant response out), including LLM inference and tool execution."
            value={
              avgTurnVal >= 1
                ? formatKpi(avgTurnVal, 's')
                : formatKpi(avgTurnVal * 1000, 'ms')
            }
            sparklineData={primarySeries(avgTurn.data)?.dataPoints}
          />
          <KpiCard
            title="Error Rate"
            description="Percentage of tool invocations that failed over a 5-minute window. Below 5% is healthy. Shows 0% when no tools are executing."
            value={formatKpi(errorRateVal, 'percent')}
            subtitle={errorRateVal > 0 ? 'errors detected' : 'no errors'}
            sparklineData={primarySeries(errorRate.data)?.dataPoints}
          />
          <KpiCard
            title="Cost / hr"
            description="Estimated LLM spend rate extrapolated to hourly. Based on token counts and model pricing. Shows $0 when no tokens are actively being consumed."
            value={formatKpi(costRateVal, 'usd/hr')}
            sparklineData={primarySeries(costRate.data)?.dataPoints}
          />
          <KpiCard
            title="Safety Blocks"
            description="Cumulative count of requests blocked by content safety filters. Includes PII detection, prompt shield, and keyword filters."
            value={safetyBlocksVal.toFixed(0)}
            subtitle={
              safetyBlocksVal > 0
                ? `${safetyBlocksVal.toFixed(0)} blocked`
                : 'none'
            }
            sparklineData={primarySeries(safetyBlocks.data)?.dataPoints}
          />
        </PanelGrid>
      </Section>

      {/* SLO Board */}
      <Section title="SLO Board" subtitle="service-level objectives" kicker="02">
        <SloBoard />
      </Section>

      {/* Recent Incidents — placeholder */}
      <Section title="Recent Incidents" kicker="03">
        <PanelCard title="Incident Log">
          <p className="text-xs text-muted-foreground py-6 text-center">
            No incidents recorded.
          </p>
        </PanelCard>
      </Section>
    </div>
  );
}

function StatBadge({
  label,
  description,
  value,
  variant,
}: {
  label: string;
  description?: string;
  value: string;
  variant: 'default' | 'warning' | 'critical';
}) {
  return (
    <div
      className={cn(
        'rounded-lg px-3 py-2 text-center min-w-[90px] relative group/badge',
        variant === 'default' && 'bg-muted/50',
        variant === 'warning' &&
          'bg-otel-warning/10 border border-otel-warning/30',
        variant === 'critical' &&
          'bg-otel-negative/10 border border-otel-negative/30',
      )}
    >
      <div className="text-[10px] font-medium text-muted-foreground uppercase tracking-wider flex items-center justify-center gap-1">
        {label}
        {description && <Info className="w-2.5 h-2.5 text-muted-foreground/50" />}
      </div>
      <div
        className={cn(
          'text-sm font-bold font-mono tabular-nums mt-0.5',
          variant === 'default' && 'text-card-foreground',
          variant === 'warning' && 'text-otel-warning',
          variant === 'critical' && 'text-otel-negative',
        )}
      >
        {value}
      </div>
      {description && (
        <span className="absolute bottom-full left-1/2 -translate-x-1/2 mb-2 px-3 py-2 text-[11px] font-normal normal-case tracking-normal leading-relaxed text-popover-foreground bg-popover border border-border rounded-lg shadow-lg w-48 text-left opacity-0 pointer-events-none group-hover/badge:opacity-100 group-hover/badge:pointer-events-auto transition-opacity duration-150 z-50">
          {description}
        </span>
      )}
    </div>
  );
}
