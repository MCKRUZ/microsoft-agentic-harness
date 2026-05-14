import { useState } from 'react';
import { PageHeader } from '@/components/primitives/PageHeader';
import { TabNav } from '@/components/primitives/TabNav';
import { HealthTab } from './HealthTab';
import { ActivityTab } from './ActivityTab';
import { SpendTab } from './SpendTab';
import { QualityTab } from './QualityTab';

const TABS = [
  { label: 'Health', description: 'is anything wrong?' },
  { label: 'Activity', description: 'what is happening' },
  { label: 'Spend', description: 'where the money goes' },
  { label: 'Quality', description: 'tools, safety, RAG' },
];

export default function PulsePage() {
  const [activeTab, setActiveTab] = useState('Health');

  return (
    <div className="space-y-6">
      <PageHeader
        title="Pulse"
        subtitle="Health, activity, spend, and quality at a glance."
      />
      <TabNav items={TABS} active={activeTab} onChange={setActiveTab} />

      {activeTab === 'Health' && <HealthTab />}
      {activeTab === 'Activity' && <ActivityTab />}
      {activeTab === 'Spend' && <SpendTab />}
      {activeTab === 'Quality' && <QualityTab />}
    </div>
  );
}
