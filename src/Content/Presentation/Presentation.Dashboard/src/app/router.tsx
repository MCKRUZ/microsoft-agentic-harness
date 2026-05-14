import { lazy, Suspense } from 'react';
import { createBrowserRouter, Navigate } from 'react-router-dom';

const DashboardShell = lazy(() => import('@/components/layout/DashboardShell'));
const PulsePage = lazy(() => import('@/routes/Pulse/PulsePage'));
const SessionsPage = lazy(() => import('@/routes/Sessions/SessionsPage'));
const SessionDetailPage = lazy(() => import('@/routes/Sessions/SessionDetailPage'));
const SpendHubPage = lazy(() => import('@/routes/Spend/SpendHubPage'));
const TokensPage = lazy(() => import('@/routes/Tokens/TokensPage'));
const CostPage = lazy(() => import('@/routes/Cost/CostPage'));
const BudgetPage = lazy(() => import('@/routes/Budget/BudgetPage'));
const QualityHubPage = lazy(() => import('@/routes/Quality/QualityHubPage'));
const ToolsPage = lazy(() => import('@/routes/Tools/ToolsPage'));
const SafetyPage = lazy(() => import('@/routes/Safety/SafetyPage'));
const RagPage = lazy(() => import('@/routes/Rag/RagPage'));
const CatalogPage = lazy(() => import('@/routes/Catalog/CatalogPage'));
const GovernancePage = lazy(() => import('@/routes/Governance/GovernancePage'));

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
      { index: true, element: <Navigate to="/pulse" replace /> },

      // Observe
      { path: 'pulse', element: <LazyWrapper><PulsePage /></LazyWrapper> },
      { path: 'sessions', element: <LazyWrapper><SessionsPage /></LazyWrapper> },
      { path: 'sessions/:sessionId', element: <LazyWrapper><SessionDetailPage /></LazyWrapper> },

      // Spend
      { path: 'spend', element: <LazyWrapper><SpendHubPage /></LazyWrapper> },
      { path: 'spend/tokens', element: <LazyWrapper><TokensPage /></LazyWrapper> },
      { path: 'spend/cost', element: <LazyWrapper><CostPage /></LazyWrapper> },
      { path: 'spend/budget', element: <LazyWrapper><BudgetPage /></LazyWrapper> },

      // Quality
      { path: 'quality', element: <LazyWrapper><QualityHubPage /></LazyWrapper> },
      { path: 'quality/tools', element: <LazyWrapper><ToolsPage /></LazyWrapper> },
      { path: 'quality/safety', element: <LazyWrapper><SafetyPage /></LazyWrapper> },
      { path: 'quality/rag', element: <LazyWrapper><RagPage /></LazyWrapper> },

      // Governance
      { path: 'governance', element: <LazyWrapper><GovernancePage /></LazyWrapper> },

      // Registry
      { path: 'catalog', element: <LazyWrapper><CatalogPage /></LazyWrapper> },

      // Legacy redirects
      { path: 'overview', element: <Navigate to="/pulse" replace /> },
      { path: 'tokens', element: <Navigate to="/spend/tokens" replace /> },
      { path: 'cost', element: <Navigate to="/spend/cost" replace /> },
      { path: 'budget', element: <Navigate to="/spend/budget" replace /> },
      { path: 'tools', element: <Navigate to="/quality/tools" replace /> },
      { path: 'safety', element: <Navigate to="/quality/safety" replace /> },
      { path: 'rag', element: <Navigate to="/quality/rag" replace /> },
    ],
  },
]);
