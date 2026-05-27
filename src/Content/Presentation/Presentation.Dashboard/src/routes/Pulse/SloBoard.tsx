import { useSloStatus, type SloStatus, type SloVerdict } from '@/hooks/useSloStatus';
import { usePromQuery } from '@/hooks/usePromQuery';
import { PanelCard } from '@/components/panels/PanelCard';
import { PanelGrid } from '@/components/panels/PanelGrid';
import { Sparkline } from '@/components/charts/Sparkline';
import { Pill } from '@/components/primitives/Pill';
import { StatusDot } from '@/components/primitives/StatusDot';
import { cn } from '@/lib/utils';
import { primarySeries } from './pulse-helpers';

const verdictConfig: Record<
  SloVerdict,
  { label: string; pill: 'positive' | 'warning' | 'negative'; dot: 'ok' | 'warning' | 'critical' }
> = {
  Met: { label: 'MET', pill: 'positive', dot: 'ok' },
  AtRisk: { label: 'AT RISK', pill: 'warning', dot: 'warning' },
  Breached: { label: 'BREACHED', pill: 'negative', dot: 'critical' },
};

function formatSloValue(value: number, unit: string): string {
  if (value < 0) return 'N/A';
  if (unit === 'percent') return `${(value * 100).toFixed(2)}%`;
  if (unit === 'ms') return `${value.toFixed(0)} ms`;
  return value.toFixed(2);
}

function formatTarget(target: number, comparator: string, unit: string): string {
  const op = comparator === 'lt' ? '<' : comparator === 'gt' ? '>' : comparator === 'lte' ? '≤' : '≥';
  return `${op} ${formatSloValue(target, unit)}`;
}

function SloCard({ slo }: { slo: SloStatus }) {
  const sparkline = usePromQuery(slo.sparklineQuery, !!slo.sparklineQuery);
  const config = verdictConfig[slo.status];
  const sparkData = primarySeries(sparkline.data)?.dataPoints;

  return (
    <div
      data-testid={`slo-card-${slo.id}`}
      className={cn(
        'rounded-xl border bg-card p-4 flex flex-col gap-3',
        slo.status === 'Breached' && 'border-otel-negative/40',
        slo.status === 'AtRisk' && 'border-otel-warning/40',
        slo.status === 'Met' && 'border-border',
      )}
    >
      <div className="flex items-center justify-between gap-2">
        <div className="flex items-center gap-2 min-w-0">
          <StatusDot status={config.dot} size={6} />
          <span className="text-xs font-medium text-card-foreground truncate">{slo.name}</span>
        </div>
        <Pill variant={config.pill}>{config.label}</Pill>
      </div>

      <div className="flex items-end justify-between gap-4">
        <div>
          <div className="text-lg font-bold text-card-foreground">
            {formatSloValue(slo.currentValue, slo.unit)}
          </div>
          <div className="text-[10px] text-muted-foreground mt-0.5">
            Target: {formatTarget(slo.target, slo.comparator, slo.unit)}
          </div>
        </div>
        {sparkData && sparkData.length > 1 && (
          <div className="w-20">
            <Sparkline dataPoints={sparkData} />
          </div>
        )}
      </div>

      <div className="space-y-1">
        <div className="flex justify-between text-[10px] text-muted-foreground">
          <span>Error Budget</span>
          <span>{slo.errorBudgetRemainingPercent.toFixed(0)}%</span>
        </div>
        <div className="h-1.5 rounded-full bg-muted overflow-hidden">
          <div
            className={cn(
              'h-full rounded-full transition-all duration-500',
              slo.errorBudgetRemainingPercent > 50 && 'bg-emerald-500',
              slo.errorBudgetRemainingPercent > 0 && slo.errorBudgetRemainingPercent <= 50 && 'bg-amber-500',
              slo.errorBudgetRemainingPercent === 0 && 'bg-red-500',
            )}
            style={{ width: `${slo.errorBudgetRemainingPercent}%` }}
          />
        </div>
      </div>

      {slo.description && (
        <p className="text-[10px] text-muted-foreground/70 leading-relaxed">{slo.description}</p>
      )}
    </div>
  );
}

export function SloBoard() {
  const { data: slos, isLoading } = useSloStatus();

  if (isLoading) {
    return (
      <PanelCard title="Service-Level Objectives">
        <p className="text-xs text-otel-text-mute py-6 text-center">Loading SLO targets...</p>
      </PanelCard>
    );
  }

  if (!slos || slos.length === 0) {
    return (
      <PanelCard title="Service-Level Objectives">
        <p className="text-xs text-otel-text-mute py-6 text-center">
          SLO tracking requires configuration. Define targets in appsettings.json.
        </p>
      </PanelCard>
    );
  }

  return (
    <PanelGrid columns={3}>
      {slos.map((slo) => (
        <SloCard key={slo.id} slo={slo} />
      ))}
    </PanelGrid>
  );
}
