import { usePromQuery } from '@/hooks/usePromQuery';
import { KpiCard } from '@/components/panels/KpiCard';
import { PanelCard } from '@/components/panels/PanelCard';
import { PanelGrid } from '@/components/panels/PanelGrid';
import { LoadingSkeleton } from '@/components/panels/LoadingSkeleton';
import { GaugeChart } from '@/components/charts/GaugeChart';
import { TimeSeriesChart } from '@/components/charts/TimeSeriesChart';
import { HBarList } from '@/components/charts/HBarList';
import { useMetric, latestValue, formatKpi, seriesToBars } from './pulse-helpers';

const COST_RATE =
  'rate(agentic_harness_agent_tokens_cost_estimated_total[5m]) * 3600 or vector(0)';
const COST_PER_SESSION =
  'agentic_harness_agent_session_cost_sum / agentic_harness_agent_session_cost_count or vector(0)';
const COST_BY_MODEL =
  'sum by (model) (agentic_harness_agent_tokens_cost_estimated_total)';
const COST_BY_AGENT =
  'sum by (agent_name) (agentic_harness_agent_tokens_cost_estimated_total)';
const INPUT_TOKENS =
  'sum(agentic_harness_agent_tokens_input_sum) or vector(0)';
const CACHE_TOKENS =
  'sum(agentic_harness_agent_tokens_cache_read_total) or vector(0)';
const OUTPUT_TOKENS =
  'sum(agentic_harness_agent_tokens_output_sum) or vector(0)';

export function SpendTab() {
  const costToday = useMetric('cost_today');
  const cacheSavings = useMetric('cost_cache_savings');
  const budgetUtil = useMetric('budget_utilization');
  const budgetLimit = useMetric('budget_limit');
  const costRate = usePromQuery(COST_RATE);
  const costPerSession = usePromQuery(COST_PER_SESSION);
  const costByModel = usePromQuery(COST_BY_MODEL);
  const costByAgent = usePromQuery(COST_BY_AGENT);
  const inputTokens = usePromQuery(INPUT_TOKENS);
  const cacheTokens = usePromQuery(CACHE_TOKENS);
  const outputTokens = usePromQuery(OUTPUT_TOKENS);

  const anyLoading =
    costToday.isLoading ||
    cacheSavings.isLoading ||
    costRate.isLoading ||
    costPerSession.isLoading;

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

  const costVal = latestValue(costToday.data);
  const savingsVal = latestValue(cacheSavings.data);
  const costPerSessVal = latestValue(costPerSession.data);
  const burnVal = latestValue(costRate.data);
  const budgetUtilVal = latestValue(budgetUtil.data);
  const budgetLimitVal = latestValue(budgetLimit.data);

  const modelBars = seriesToBars(
    costByModel.data?.series ?? [],
    'model',
    (v) => `$${v.toFixed(4)}`,
  );
  const agentBars = seriesToBars(
    costByAgent.data?.series ?? [],
    'agent_name',
    (v) => `$${v.toFixed(4)}`,
  );

  const inputVal = latestValue(inputTokens.data);
  const cacheVal = latestValue(cacheTokens.data);
  const outputVal = latestValue(outputTokens.data);
  const totalTokens = inputVal + cacheVal + outputVal;

  return (
    <div className="space-y-6">
      <PanelGrid columns={4}>
        <KpiCard
          title="Cost Today"
          description="Cumulative estimated LLM spend since midnight UTC. Based on token counts multiplied by per-model pricing."
          value={formatKpi(costVal, 'usd')}
          unit="USD"
          sparklineData={costToday.data?.series[0]?.dataPoints}
        />
        <KpiCard
          title="Cache Savings"
          description="Estimated cost saved by prompt caching. Cached tokens are billed at a reduced rate compared to fresh input tokens."
          value={formatKpi(savingsVal, 'usd')}
          subtitle="saved via prompt caching"
        />
        <KpiCard
          title="Cost / Session"
          description="Average cost per completed agent session. Includes all LLM calls, tool invocations, and cached tokens."
          value={`$${costPerSessVal.toFixed(4)}`}
          subtitle="median today"
        />
        <KpiCard
          title="Burn Rate"
          description="Current LLM spend rate extrapolated to hourly based on a 5-minute rolling window. Shows $0 when idle."
          value={formatKpi(burnVal, 'usd/hr')}
          sparklineData={costRate.data?.series[0]?.dataPoints}
        />
      </PanelGrid>

      {/* Spend trajectory + Budget gauge */}
      <div className="grid grid-cols-1 lg:grid-cols-[2fr_1fr] gap-4">
        <PanelCard
          title="Spend trajectory"
          description="cumulative cost today"
        >
          <TimeSeriesChart
            series={costToday.data?.series ?? []}
            unit="usd"
          />
        </PanelCard>

        <PanelCard
          title="Daily budget"
          description={
            budgetLimitVal > 0
              ? `$${budgetLimitVal.toFixed(2)} cap`
              : 'no cap set'
          }
        >
          <GaugeChart
            value={budgetUtilVal}
            max={1}
            label="Used"
            unit="percent"
            thresholds={{ warn: 0.75, critical: 0.9 }}
          />
          {burnVal > 0 && budgetLimitVal > 0 && budgetUtilVal < 1 && (
            <p className="text-[11px] text-otel-text-dim text-center mt-2">
              At current burn, exhausts in ~
              {((budgetLimitVal * (1 - budgetUtilVal)) / burnVal).toFixed(1)}h
            </p>
          )}
        </PanelCard>
      </div>

      {/* By model / By agent */}
      <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
        <PanelCard title="By model" description="cost today">
          <HBarList items={modelBars} />
        </PanelCard>
        <PanelCard title="By agent" description="cost today">
          <HBarList items={agentBars} />
        </PanelCard>
      </div>

      {/* Token shape */}
      <PanelCard title="Token shape" description="input vs. cache vs. output">
        {totalTokens > 0 ? (
          <div className="space-y-3">
            <div className="flex h-8 rounded-lg overflow-hidden">
              <div
                className="bg-[var(--chart-1)]"
                style={{ width: `${(inputVal / totalTokens) * 100}%` }}
                title={`Input: ${formatKpi(inputVal, 'count')}`}
              />
              <div
                className="bg-[var(--chart-2)]"
                style={{ width: `${(cacheVal / totalTokens) * 100}%` }}
                title={`Cache: ${formatKpi(cacheVal, 'count')}`}
              />
              <div
                className="bg-[var(--chart-3)]"
                style={{ width: `${(outputVal / totalTokens) * 100}%` }}
                title={`Output: ${formatKpi(outputVal, 'count')}`}
              />
            </div>
            <div className="flex items-center gap-4 text-[11px]">
              <span className="flex items-center gap-1.5">
                <span className="w-2 h-2 rounded-full bg-[var(--chart-1)]" />
                Input {formatKpi(inputVal, 'count')}
              </span>
              <span className="flex items-center gap-1.5">
                <span className="w-2 h-2 rounded-full bg-[var(--chart-2)]" />
                Cache {formatKpi(cacheVal, 'count')}
              </span>
              <span className="flex items-center gap-1.5">
                <span className="w-2 h-2 rounded-full bg-[var(--chart-3)]" />
                Output {formatKpi(outputVal, 'count')}
              </span>
            </div>
          </div>
        ) : (
          <p className="text-xs text-otel-text-mute py-6 text-center">
            No token data yet
          </p>
        )}
      </PanelCard>
    </div>
  );
}
