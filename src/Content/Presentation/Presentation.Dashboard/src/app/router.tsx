import { lazy, Suspense } from 'react';
import { createBrowserRouter, Navigate } from 'react-router-dom';

const DashboardShell = lazy(() => import('@/components/layout/DashboardShell'));
const OverviewPage = lazy(() => import('@/routes/Overview/OverviewPage'));
const TokensPage = lazy(() => import('@/routes/Tokens/TokensPage'));
const CostPage = lazy(() => import('@/routes/Cost/CostPage'));
const SessionsPage = lazy(() => import('@/routes/Sessions/SessionsPage'));
const ToolsPage = lazy(() => import('@/routes/Tools/ToolsPage'));
const SafetyPage = lazy(() => import('@/routes/Safety/SafetyPage'));
const RagPage = lazy(() => import('@/routes/Rag/RagPage'));
const BudgetPage = lazy(() => import('@/routes/Budget/BudgetPage'));

function LazyWrapper({ children }: { children: React.ReactNode }) {
  return (
    <Suspense fallback={<div className="flex items-center justify-center h-full text-muted-foreground">Loading...</div>}>
      {children}
    </Suspense>
  );
}

export const router = createBrowserRouter([
  {
    path: '/',
    element: <LazyWrapper><DashboardShell /></LazyWrapper>,
    children: [
      { index: true, element: <Navigate to="/overview" replace /> },
      { path: 'overview', element: <LazyWrapper><OverviewPage /></LazyWrapper> },
      { path: 'tokens', element: <LazyWrapper><TokensPage /></LazyWrapper> },
      { path: 'cost', element: <LazyWrapper><CostPage /></LazyWrapper> },
      { path: 'sessions', element: <LazyWrapper><SessionsPage /></LazyWrapper> },
      { path: 'tools', element: <LazyWrapper><ToolsPage /></LazyWrapper> },
      { path: 'safety', element: <LazyWrapper><SafetyPage /></LazyWrapper> },
      { path: 'rag', element: <LazyWrapper><RagPage /></LazyWrapper> },
      { path: 'budget', element: <LazyWrapper><BudgetPage /></LazyWrapper> },
    ],
  },
]);
