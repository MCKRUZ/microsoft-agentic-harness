import { AuthenticatedTemplate, UnauthenticatedTemplate, useMsal } from '@azure/msal-react';
import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom';
import { DashboardLayout } from '@/components/layout/DashboardLayout';
import { ChatView } from '@/views/ChatView';
import { AgentsView } from '@/views/AgentsView';
import { ToolsView } from '@/views/ToolsView';
import { ResourcesView } from '@/views/ResourcesView';
import { PromptsView } from '@/views/PromptsView';
import { IS_AUTH_DISABLED } from '@/lib/devAuth';

function LoginView() {
  const { instance } = useMsal();

  return (
    <div className="flex items-center justify-center h-screen">
      <div className="text-center space-y-4">
        <h1 className="text-2xl font-bold">AgentHub</h1>
        <p className="text-muted-foreground">Sign in with your Microsoft account to continue</p>
        <button
          onClick={() => { void instance.loginRedirect(); }}
          className="px-4 py-2 bg-primary text-primary-foreground rounded hover:opacity-90"
        >
          Sign in with Microsoft
        </button>
      </div>
    </div>
  );
}

const AppRoutes = () => (
  <Routes>
    <Route element={<DashboardLayout />}>
      <Route index element={<Navigate to="/chat" replace />} />
      <Route path="chat" element={<ChatView />} />
      <Route path="agents" element={<AgentsView />} />
      <Route path="tools" element={<ToolsView />} />
      <Route path="resources" element={<ResourcesView />} />
      <Route path="prompts" element={<PromptsView />} />
      <Route path="*" element={<Navigate to="/chat" replace />} />
    </Route>
  </Routes>
);

export function AppRouter() {
  return (
    <BrowserRouter>
      {IS_AUTH_DISABLED ? (
        <AppRoutes />
      ) : (
        <>
          <AuthenticatedTemplate>
            <AppRoutes />
          </AuthenticatedTemplate>
          <UnauthenticatedTemplate>
            <LoginView />
          </UnauthenticatedTemplate>
        </>
      )}
    </BrowserRouter>
  );
}
