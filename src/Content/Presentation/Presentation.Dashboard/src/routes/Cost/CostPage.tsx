import { usePromQuery } from '@/hooks/usePromQuery';
import { metricCatalog } from '@/config/metricCatalog';
import { KpiCard } from '@/components/panels/KpiCard';
import { PanelCard } from '@/components/panels/PanelCard';
import { PanelGrid } from '@/components/panels/PanelGrid';
import { LoadingSkeleton } from '@/components/panels/LoadingSkeleton';
import { TimeSeriesChart } from '@/components/charts/TimeSeriesChart';
import { MetricPieChart } from '@/components/charts/PieChart';
import { GaugeChart } from '@/components/charts/GaugeChart';

function latestValue(data: ReturnType<typeof usePromQuery>['data']): number {
  const dp = data?.series[0]?.dataPoints;
  if (!dp || dp.length === 0) return 0;
  return parseFloat(dp[dp.length - 1]!.value) || 0;
}

export default function CostPage() {
  const costTotal = usePromQuery(metricCatalog['cost_total']!.query);
  const costRate = usePromQuery(metricCatalog['cost_rate']!.query);
  const byModel = usePromQuery(metricCatalog['cost_by_model']!.query);
  const cacheSavings = usePromQuery(metricCatalog['cost_cache_savings']!.query);
  const budgetRemaining = usePromQuery(metricCatalog['cost_budget_remaining']!.query);

  if (costTotal.isLoading) {
    return (
      <div className="space-y-6">
        <h1 className="text-xl font-bold text-foreground">Cost Analytics</h1>
        <PanelGrid columns={3}>
          {Array.from({ length: 3 }).map((_, i) => <LoadingSkeleton key={i} />)}
        </PanelGrid>
      </div>
    );
  }

  const totalCost = latestValue(costTotal.data);
  const savings = latestValue(cacheSavings.data);
  const remaining = latestValue(budgetRemaining.data);

  return (
    <div className="space-y-6">
      <h1 className="text-xl font-bold text-foreground">Cost Analytics</h1>

      <PanelGrid columns={3}>
        <KpiCard title="Total Cost" value={`$${totalCost.toFixed(4)}`} unit="USD" sparklineData={costTotal.data?.series[0]?.dataPoints} />
        <KpiCard title="Cache Savings" value={`$${savings.toFixed(4)}`} unit="USD" sparklineData={cacheSavings.data?.series[0]?.dataPoints} />
        <KpiCard title="Budget Remaining" value={`$${remaining.toFixed(2)}`} unit="USD" sparklineData={budgetRemaining.data?.series[0]?.dataPoints} />
      </PanelGrid>

      <PanelGrid columns={2}>
        <PanelCard title="Cost Rate" description="USD burn rate per hour">
          <TimeSeriesChart series={costRate.data?.series ?? []} unit="usd" />
        </PanelCard>
        <PanelCard title="Cost by Model">
          <MetricPieChart series={byModel.data?.series ?? []} unit="usd" />
        </PanelCard>
      </PanelGrid>

      <PanelGrid columns={2}>
        <PanelCard title="Budget Progress">
          <GaugeChart
            value={totalCost}
            max={totalCost + remaining > 0 ? totalCost + remaining : 100}
            label="Spent"
            unit="usd"
            thresholds={{ warn: (totalCost + remaining) * 0.75, critical: (totalCost + remaining) * 0.9 }}
          />
        </PanelCard>
        <PanelCard title="Cache ROI">
          <div className="flex flex-col items-center justify-center h-[160px]">
            <div className="text-3xl font-bold text-card-foreground">
              {totalCost > 0 ? `${((savings / totalCost) * 100).toFixed(1)}%` : '—'}
            </div>
            <div className="text-xs text-muted-foreground mt-1">of total cost saved via caching</div>
            <div className="text-sm text-muted-foreground mt-3">
              ${savings.toFixed(4)} saved / ${totalCost.toFixed(4)} total
            </div>
          </div>
        </PanelCard>
      </PanelGrid>
    </div>
  );
}
