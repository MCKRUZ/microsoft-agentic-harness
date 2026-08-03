import { Outlet } from 'react-router';
import { Sidebar } from './Sidebar';
import { Topbar } from './Topbar';
import { AgentPanel } from '@/components/agent/AgentPanel';
import { useTelemetryStream } from '@/realtime/useTelemetryStream';

export default function DashboardShell() {
  useTelemetryStream();

  return (
    <div className="flex h-screen overflow-hidden">
      <Sidebar />
      <div className="flex-1 flex flex-col overflow-hidden">
        <Topbar />
        <main aria-label="Dashboard content" className="flex-1 overflow-y-auto p-6">
          <Outlet />
        </main>
      </div>
      {/* Mounted once at the shell level so the agent panel overlays every page. */}
      <AgentPanel />
    </div>
  );
}
