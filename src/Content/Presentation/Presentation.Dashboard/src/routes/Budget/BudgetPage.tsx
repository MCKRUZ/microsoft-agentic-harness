import { usePromQuery } from '@/hooks/usePromQuery';
import { metricCatalog } from '@/config/metricCatalog';
import { KpiCard } from '@/components/panels/KpiCard';
import { PanelCard } from '@/components/panels/PanelCard';
import { PanelGrid } from '@/components/panels/PanelGrid';
import { LoadingSkeleton } from '@/components/panels/LoadingSkeleton';
import { TimeSeriesChart } from '@/components/charts/TimeSeriesChart';
import { GaugeChart } from '@/components/charts/GaugeChart';

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
  const alerts = usePromQuery(metricCatalog['budget_alerts_total']!.query);

  if (spent.isLoading) {
    return (
      <div className="space-y-6">
        <h1 className="text-xl font-bold text-foreground">Budget Management</h1>
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
      <h1 className="text-xl font-bold text-foreground">Budget Management</h1>

      <PanelGrid columns={3}>
        <KpiCard title="Total Spent" value={`$${spentVal.toFixed(4)}`} unit="USD" sparklineData={spent.data?.series[0]?.dataPoints} />
        <KpiCard title="Budget Limit" value={`$${limitVal.toFixed(2)}`} unit="USD" />
        <KpiCard title="Remaining" value={`$${remainingVal.toFixed(2)}`} unit="USD" sparklineData={remaining.data?.series[0]?.dataPoints} />
      </PanelGrid>

      <PanelGrid columns={2}>
        <PanelCard title="Budget Utilization">
          <GaugeChart
            value={utilizationVal}
            max={1}
            label="Used"
            unit="percent"
            thresholds={{ warn: 0.75, critical: 0.9 }}
          />
        </PanelCard>
        <PanelCard title="Spend Gauge">
          <GaugeChart
            value={spentVal}
            max={limitVal > 0 ? limitVal : 100}
            label={`$${spentVal.toFixed(2)} of $${limitVal.toFixed(2)}`}
            unit="usd"
            thresholds={{ warn: limitVal * 0.75, critical: limitVal * 0.9 }}
          />
        </PanelCard>
      </PanelGrid>

      <PanelGrid columns={2}>
        <PanelCard title="Spend Rate" description="USD burn rate per hour">
          <TimeSeriesChart series={spendRate.data?.series ?? []} unit="usd" />
        </PanelCard>
        <PanelCard title="Budget Alerts">
          <div className="flex flex-col items-center justify-center h-[200px]">
            <div className="text-4xl font-bold text-card-foreground">
              {latestValue(alerts.data).toFixed(0)}
            </div>
            <div className="text-sm text-muted-foreground mt-2">threshold alerts triggered</div>
          </div>
        </PanelCard>
      </PanelGrid>
    </div>
  );
}
