import { NavLink, useLocation } from 'react-router-dom';
import {
  Activity, Users, TrendingUp, Coins, DollarSign, Wallet,
  Wrench, ShieldCheck, Database, LayoutGrid, ChevronDown, Shield,
} from 'lucide-react';
import { cn } from '@/lib/utils';
import { useState } from 'react';

interface NavGroup {
  label: string;
  items: NavItem[];
  collapsible?: boolean;
}

interface NavItem {
  to: string;
  label: string;
  icon: React.ComponentType<{ className?: string }>;
  meta?: string;
}

const navGroups: NavGroup[] = [
  {
    label: 'Observe',
    items: [
      { to: '/pulse', label: 'Pulse', icon: Activity },
      { to: '/sessions', label: 'Sessions', icon: Users },
    ],
  },
  {
    label: 'Spend',
    collapsible: true,
    items: [
      { to: '/spend', label: 'Overview', icon: TrendingUp },
      { to: '/spend/tokens', label: 'Tokens', icon: Coins },
      { to: '/spend/cost', label: 'Cost', icon: DollarSign },
      { to: '/spend/budget', label: 'Budget', icon: Wallet },
    ],
  },
  {
    label: 'Quality',
    collapsible: true,
    items: [
      { to: '/quality', label: 'Overview', icon: TrendingUp },
      { to: '/quality/tools', label: 'Tools', icon: Wrench },
      { to: '/quality/safety', label: 'Safety', icon: ShieldCheck },
      { to: '/quality/rag', label: 'RAG', icon: Database },
    ],
  },
  {
    label: 'Governance',
    items: [
      { to: '/governance', label: 'Governance', icon: Shield },
    ],
  },
  {
    label: 'Registry',
    items: [
      { to: '/catalog', label: 'Catalog', icon: LayoutGrid },
    ],
  },
];

function NavGroupSection({ group }: { group: NavGroup }) {
  const location = useLocation();
  const isGroupActive = group.items.some((item) =>
    item.to === '/' ? location.pathname === '/' : location.pathname.startsWith(item.to),
  );
  const [expanded, setExpanded] = useState(isGroupActive || !group.collapsible);

  return (
    <div>
      <button
        onClick={() => group.collapsible && setExpanded(!expanded)}
        className={cn(
          'flex items-center justify-between w-full px-2 py-1.5 text-[10px] font-semibold tracking-[0.16em] uppercase',
          group.collapsible ? 'cursor-pointer hover:text-sidebar-foreground' : 'cursor-default',
          isGroupActive ? 'text-otel-accent' : 'text-otel-text-mute',
        )}
      >
        {group.label}
        {group.collapsible && (
          <ChevronDown
            className={cn('h-3 w-3 transition-transform', expanded && 'rotate-180')}
          />
        )}
      </button>
      {expanded && (
        <div className="mt-0.5 space-y-0.5">
          {group.items.map((item) => (
            <SidebarNavLink key={item.to} item={item} />
          ))}
        </div>
      )}
    </div>
  );
}

function SidebarNavLink({ item }: { item: NavItem }) {
  const { to, label, icon: Icon, meta } = item;

  return (
    <NavLink
      to={to}
      end={to === '/spend' || to === '/quality'}
      className={({ isActive }) =>
        cn(
          'flex items-center gap-2.5 px-2 py-1.5 rounded-md text-xs font-medium transition-colors',
          isActive
            ? 'bg-sidebar-accent text-sidebar-foreground border-l-2 border-otel-accent'
            : 'text-otel-text-dim hover:text-sidebar-foreground hover:bg-sidebar-accent/50 border-l-2 border-transparent',
        )
      }
    >
      <Icon className="h-3.5 w-3.5 shrink-0" aria-hidden="true" />
      <span className="flex-1">{label}</span>
      {meta && <span className="text-[9px] font-mono text-otel-text-mute tabular-nums">{meta}</span>}
    </NavLink>
  );
}

export function Sidebar() {
  return (
    <aside className="w-52 border-r border-sidebar-border bg-sidebar flex flex-col h-full">
      <div className="px-3 pt-4 pb-3 border-b border-sidebar-border">
        <div className="text-[11px] font-bold tracking-[0.16em] text-otel-accent">
          OTEL · HARNESS
        </div>
        <div className="text-[10px] text-otel-text-mute mt-0.5">
          agentic-harness / local
        </div>
      </div>

      <nav aria-label="Dashboard navigation" className="flex-1 p-2 space-y-3 overflow-y-auto">
        {navGroups.map((group) => (
          <NavGroupSection key={group.label} group={group} />
        ))}
      </nav>

      <div className="mx-2 mb-2 p-2.5 bg-otel-panel-2 rounded-md border border-sidebar-border">
        <div className="text-[9px] text-otel-text-mute tracking-[0.16em] uppercase font-semibold mb-1.5">
          Daily Budget
        </div>
        <div className="h-1 bg-background/10 rounded-sm overflow-hidden mb-1.5">
          <div className="h-full bg-otel-accent rounded-sm" style={{ width: '56.6%' }} />
        </div>
        <div className="flex justify-between text-[10px] font-mono tabular-nums">
          <span className="text-sidebar-foreground">$11.31</span>
          <span className="text-otel-text-mute">/ $20</span>
        </div>
      </div>
    </aside>
  );
}
