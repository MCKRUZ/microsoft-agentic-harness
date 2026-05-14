import { useNavigate } from 'react-router-dom';
import { PageHeader } from '@/components/primitives/PageHeader';
import { PanelGrid } from '@/components/panels/PanelGrid';
import { Coins, DollarSign, Wallet } from 'lucide-react';

const sections = [
  { to: '/spend/tokens', label: 'Tokens', description: 'Token volume by model, agent, and session', icon: Coins },
  { to: '/spend/cost', label: 'Cost', description: 'USD spend breakdown by model, agent, and environment', icon: DollarSign },
  { to: '/spend/budget', label: 'Budget', description: 'Daily and monthly cap utilization with alerts', icon: Wallet },
];

export default function SpendHubPage() {
  const navigate = useNavigate();

  return (
    <div className="space-y-6">
      <PageHeader title="Spend" subtitle="Token consumption and cost analysis" />
      <PanelGrid columns={3}>
        {sections.map(({ to, label, description, icon: Icon }) => (
          <button
            key={to}
            onClick={() => navigate(to)}
            className="bg-card border border-border rounded-lg p-5 text-left hover:border-otel-accent/40 transition-colors cursor-pointer"
          >
            <div className="flex items-center gap-2.5 mb-2">
              <Icon className="h-4 w-4 text-otel-accent" />
              <span className="text-sm font-semibold text-foreground">{label}</span>
            </div>
            <p className="text-xs text-otel-text-dim leading-relaxed">{description}</p>
          </button>
        ))}
      </PanelGrid>
    </div>
  );
}
