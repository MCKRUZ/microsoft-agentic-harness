import { AuthenticatedTemplate, UnauthenticatedTemplate, useMsal } from '@azure/msal-react';
import { BrowserRouter, Routes, Route } from 'react-router-dom';
import { AppShell } from '@/components/layout/AppShell';

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

export function AppRouter() {
  return (
    <BrowserRouter>
      <AuthenticatedTemplate>
        <Routes>
          <Route path="/" element={<AppShell />} />
          <Route path="*" element={<AppShell />} />
        </Routes>
      </AuthenticatedTemplate>
      <UnauthenticatedTemplate>
        <LoginView />
      </UnauthenticatedTemplate>
    </BrowserRouter>
  );
}
