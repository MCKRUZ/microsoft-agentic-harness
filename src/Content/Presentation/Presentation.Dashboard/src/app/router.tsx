/* eslint-disable react-refresh/only-export-components --
 * This module's only export is `router`, but it declares 20 lazy() component bindings, so the
 * rule fires once per binding — 20 of the repo's errors from one file.
 *
 * The rule protects Fast Refresh, which cannot apply to a route table anyway: changing the
 * route graph remounts the tree, and react-router recreates the browser router wholesale. The
 * suggested remedy (move the lazy() consts to their own module) would add a file whose sole
 * purpose is to satisfy a rule that has no effect here, and it would separate each route's
 * import from the path it serves — the one place a reader wants them together.
 *
 * Scoped to this file only. If a real component is ever added and exported here, move it out
 * rather than widening the disable.
 */
import { lazy, Suspense } from 'react';
import { createBrowserRouter, Navigate } from 'react-router-dom';

const DashboardShell = lazy(() => import('@/components/layout/DashboardShell'));
const PulsePage = lazy(() => import('@/routes/Pulse/PulsePage'));
const SessionsPage = lazy(() => import('@/routes/Sessions/SessionsPage'));
const SessionDetailPage = lazy(() => import('@/routes/Sessions/SessionDetailPage'));
const ContextInspectorPage = lazy(() => import('@/routes/Sessions/ContextInspectorPage'));
const ToolInvocationPage = lazy(() => import('@/routes/Sessions/ToolInvocationPage'));
const MessageBodyPage = lazy(() => import('@/routes/Sessions/MessageBodyPage'));
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
const EvalsListPage = lazy(() => import('@/routes/Evals/EvalsListPage'));
const EvalRunDetailPage = lazy(() => import('@/routes/Evals/EvalRunDetailPage'));

// Dev-only sandbox. `import.meta.env.DEV` is statically replaced by Vite, so in
// prod the ternary resolves to `null` at build time and the dynamic import() is
// dropped from the bundle entirely — no DesignSystemPage chunk ships to users.
const DesignSystemPage = import.meta.env.DEV
  ? lazy(() => import('@/routes/DesignSystem/DesignSystemPage'))
  : null;

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
      { path: 'sessions/:sessionId/context', element: <LazyWrapper><ContextInspectorPage /></LazyWrapper> },
      { path: 'sessions/:sessionId/tools/:invocationId', element: <LazyWrapper><ToolInvocationPage /></LazyWrapper> },
      { path: 'sessions/:sessionId/files/:messageId', element: <LazyWrapper><MessageBodyPage /></LazyWrapper> },

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

      // Evals (Sub-phase 5.4)
      { path: 'evals', element: <LazyWrapper><EvalsListPage /></LazyWrapper> },
      { path: 'evals/:runId', element: <LazyWrapper><EvalRunDetailPage /></LazyWrapper> },

      // Registry
      { path: 'catalog', element: <LazyWrapper><CatalogPage /></LazyWrapper> },

      // Dev-only: Foresight design-system sandbox. Spread is empty in prod, so
      // the route does not exist and React Router falls through to the parent.
      ...(DesignSystemPage
        ? [{ path: 'design-system', element: <LazyWrapper><DesignSystemPage /></LazyWrapper> }]
        : []),

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
