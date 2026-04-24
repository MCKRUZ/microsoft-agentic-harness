import { NavLink } from 'react-router-dom';
import {
  LayoutDashboard, Coins, DollarSign, Users, Wrench,
  ShieldCheck, Database, Wallet,
} from 'lucide-react';
import { cn } from '@/lib/utils';

const navItems = [
  { to: '/overview', label: 'Overview', icon: LayoutDashboard },
  { to: '/tokens', label: 'Tokens', icon: Coins },
  { to: '/cost', label: 'Cost', icon: DollarSign },
  { to: '/sessions', label: 'Sessions', icon: Users },
  { to: '/tools', label: 'Tools', icon: Wrench },
  { to: '/safety', label: 'Safety', icon: ShieldCheck },
  { to: '/rag', label: 'RAG', icon: Database },
  { to: '/budget', label: 'Budget', icon: Wallet },
];

export function Sidebar() {
  return (
    <aside className="w-56 border-r border-sidebar-border bg-sidebar flex flex-col h-full">
      <div className="p-4 border-b border-sidebar-border">
        <h1 className="text-sm font-semibold text-sidebar-foreground tracking-tight">
          Telemetry Dashboard
        </h1>
      </div>
      <nav aria-label="Dashboard navigation" className="flex-1 p-3 space-y-1">
        {navItems.map(({ to, label, icon: Icon }) => (
          <NavLink
            key={to}
            to={to}
            className={({ isActive }) =>
              cn(
                'flex items-center gap-3 px-3 py-2 rounded-md text-sm font-medium transition-colors',
                isActive
                  ? 'bg-sidebar-accent text-sidebar-accent-foreground'
                  : 'text-sidebar-foreground/70 hover:text-sidebar-foreground hover:bg-sidebar-accent/50',
              )
            }
          >
            <Icon className="h-4 w-4 shrink-0" aria-hidden="true" />
            {label}
          </NavLink>
        ))}
      </nav>
    </aside>
  );
}
