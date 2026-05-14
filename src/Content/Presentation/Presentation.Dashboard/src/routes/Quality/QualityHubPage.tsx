import { useNavigate } from 'react-router-dom';
import { PageHeader } from '@/components/primitives/PageHeader';
import { PanelGrid } from '@/components/panels/PanelGrid';
import { Wrench, ShieldCheck, Database } from 'lucide-react';

const sections = [
  { to: '/quality/tools', label: 'Tools', description: 'Reliability, latency, and error rates per tool', icon: Wrench },
  { to: '/quality/safety', label: 'Safety', description: 'Content safety blocks, flags, and redactions', icon: ShieldCheck },
  { to: '/quality/rag', label: 'RAG', description: 'Retrieval performance, hit rates, and source quality', icon: Database },
];

export default function QualityHubPage() {
  const navigate = useNavigate();

  return (
    <div className="space-y-6">
      <PageHeader title="Quality" subtitle="Tool reliability, safety, and retrieval performance" />
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
