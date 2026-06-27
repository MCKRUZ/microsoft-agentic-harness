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

// Agent-interaction surface (folded in from the former WebUI). Its own shell
// mounts the SignalR conversation hub; the views use named exports, so each
// lazy import maps the named member onto `default`.
const ChatShell = lazy(() => import('@/components/layout/ChatShell'));
const ChatView = lazy(() => import('@/views/ChatView').then((m) => ({ default: m.ChatView })));
const AgentsView = lazy(() => import('@/views/AgentsView').then((m) => ({ default: m.AgentsView })));
const ToolsView = lazy(() => import('@/views/ToolsView').then((m) => ({ default: m.ToolsView })));
const ResourcesView = lazy(() => import('@/views/ResourcesView').then((m) => ({ default: m.ResourcesView })));
const PromptsView = lazy(() => import('@/views/PromptsView').then((m) => ({ default: m.PromptsView })));

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

  // Agent interaction (chat + MCP browsers) under its own shell, which mounts
  // the SignalR conversation hub. Namespaced under /agent so 'tools' here does
  // not collide with the legacy '/tools' → '/quality/tools' redirect above.
  {
    path: '/agent',
    element: <LazyWrapper><ChatShell /></LazyWrapper>,
    children: [
      { index: true, element: <Navigate to="/agent/chat" replace /> },
      { path: 'chat', element: <LazyWrapper><ChatView /></LazyWrapper> },
      { path: 'agents', element: <LazyWrapper><AgentsView /></LazyWrapper> },
      { path: 'tools', element: <LazyWrapper><ToolsView /></LazyWrapper> },
      { path: 'resources', element: <LazyWrapper><ResourcesView /></LazyWrapper> },
      { path: 'prompts', element: <LazyWrapper><PromptsView /></LazyWrapper> },
    ],
  },
]);
