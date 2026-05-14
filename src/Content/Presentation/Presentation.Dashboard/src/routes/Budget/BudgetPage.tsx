import { usePromQuery } from '@/hooks/usePromQuery';
import { metricCatalog } from '@/config/metricCatalog';
import { KpiCard } from '@/components/panels/KpiCard';
import { PanelCard } from '@/components/panels/PanelCard';
import { PanelGrid } from '@/components/panels/PanelGrid';
import { LoadingSkeleton } from '@/components/panels/LoadingSkeleton';
import { TimeSeriesChart } from '@/components/charts/TimeSeriesChart';
import { PageHeader } from '@/components/primitives/PageHeader';
import { Section } from '@/components/primitives/Section';
import { ArcGauge } from '@/components/primitives/ArcGauge';

function latestValue(data: ReturnType<typeof usePromQuery>['data']): number {
  const dp = data?.series[0]?.dataPoints;
  if (!dp || dp.length === 0) return 0;
  return parseFloat(dp[dp.length - 1]!.value) || 0;
}

export default function BudgetPage() {
  const spent = usePromQuery(metricCatalog['budget_spent']!.query);
  const limit = usePromQuery(metricCatalog['budget_limit']!.query);
  const remaining = usePromQuery(metricCatalog['budget_remaining']!.query);
  const utilization = usePromQuery(metricCatalog['budget_utilization']!.query);
  const spendRate = usePromQuery(metricCatalog['budget_spend_rate']!.query);
  const statusVal = usePromQuery(metricCatalog['budget_status']!.query);

  if (spent.isLoading) {
    return (
      <div className="space-y-6">
        <PageHeader title="Budget" subtitle="Daily and monthly cap utilization" />
        <PanelGrid columns={3}>
          {Array.from({ length: 3 }).map((_, i) => <LoadingSkeleton key={i} />)}
        </PanelGrid>
      </div>
    );
  }

  const spentVal = latestValue(spent.data);
  const limitVal = latestValue(limit.data);
  const remainingVal = latestValue(remaining.data);
  const utilizationVal = latestValue(utilization.data);

  return (
    <div className="space-y-6">
      <PageHeader title="Budget" subtitle="Daily and monthly cap utilization" />

      <Section title="Budget Metrics" kicker="01">
        <PanelGrid columns={3}>
          <KpiCard
            title="Total Spent"
            description="Cumulative USD spent against the configured budget cap in the current period. Shows $0 when no LLM API calls have incurred cost."
            value={`$${spentVal.toFixed(4)}`}
            unit="USD"
            sparklineData={spent.data?.series[0]?.dataPoints}
          />
          <KpiCard
            title="Budget Limit"
            description="Maximum USD spend allowed for the current budget period, set in application configuration. Shows $0 when no budget cap has been configured."
            value={`$${limitVal.toFixed(2)}`}
            unit="USD"
          />
          <KpiCard
            title="Remaining"
            description="USD left before the budget cap is reached and requests may be throttled. Shows $0 when the budget is fully consumed or no cap is configured."
            value={`$${remainingVal.toFixed(2)}`}
            unit="USD"
            sparklineData={remaining.data?.series[0]?.dataPoints}
          />
        </PanelGrid>
      </Section>

      <Section title="Utilization" kicker="02">
        <PanelGrid columns={2}>
          <PanelCard title="Budget Utilization">
            <div className="flex items-center justify-center py-4">
              <ArcGauge
                value={spentVal}
                max={limitVal || 1}
                size={160}
                color={utilizationVal > 0.75 ? 'var(--otel-warning)' : 'var(--otel-accent)'}
                label={`${(utilizationVal * 100).toFixed(0)}%`}
                subtitle="of daily cap"
                thickness={12}
              />
            </div>
          </PanelCard>
          <PanelCard title="Spend Rate" description="USD burn rate per hour">
            <TimeSeriesChart series={spendRate.data?.series ?? []} unit="usd" />
          </PanelCard>
        </PanelGrid>
      </Section>

      <Section title="Alerts" kicker="03">
        <PanelGrid columns={1}>
          <PanelCard title="Budget Status">
            <div className="flex flex-col items-center justify-center h-[200px]">
              {(() => {
                const status = latestValue(statusVal.data);
                const label = status >= 2 ? 'CRITICAL' : status >= 1 ? 'WARNING' : 'OK';
                const color = status >= 2 ? 'text-red-500' : status >= 1 ? 'text-yellow-500' : 'text-green-500';
                return (
                  <>
                    <div className={`text-4xl font-bold ${color}`}>{label}</div>
                    <div className="text-sm text-muted-foreground mt-2">current budget health</div>
                  </>
                );
              })()}
            </div>
          </PanelCard>
        </PanelGrid>
      </Section>
    </div>
  );
}
