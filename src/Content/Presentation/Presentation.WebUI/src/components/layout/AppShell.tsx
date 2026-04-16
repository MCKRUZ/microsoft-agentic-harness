import { Header } from './Header';
import { SplitPanel } from './SplitPanel';
import { ChatPanel } from '@/features/chat/ChatPanel';

export function AppShell() {
  return (
    <div className="flex flex-col h-screen overflow-hidden">
      <Header />
      <div className="flex-1 overflow-hidden min-h-0">
        <SplitPanel left={<ChatPanel />} right={<div />} />
      </div>
    </div>
  );
}
