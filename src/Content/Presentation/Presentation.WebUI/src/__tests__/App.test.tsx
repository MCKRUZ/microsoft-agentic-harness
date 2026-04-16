import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import App from '@/app/App';

// Use vi.hoisted so the object is available inside the vi.mock factory
const mocks = vi.hoisted(() => ({ showAuthenticated: { value: true } }));

vi.mock('@azure/msal-react', () => ({
  MsalProvider: ({ children }: { children: React.ReactNode }) => <>{children}</>,
  AuthenticatedTemplate: ({ children }: { children: React.ReactNode }) =>
    mocks.showAuthenticated.value ? <>{children}</> : null,
  UnauthenticatedTemplate: ({ children }: { children: React.ReactNode }) =>
    !mocks.showAuthenticated.value ? <>{children}</> : null,
  useMsal: () => ({
    instance: {
      logoutRedirect: vi.fn().mockResolvedValue(undefined),
      loginRedirect: vi.fn().mockResolvedValue(undefined),
    },
    accounts: [{ name: 'Test User', username: 'test@test.com' }],
    inProgress: 'none',
  }),
}));

vi.mock('@/lib/authConfig', () => ({
  msalConfig: {},
  loginRequest: { scopes: [] },
  msalInstance: {
    initialize: vi.fn().mockResolvedValue(undefined),
    logoutRedirect: vi.fn().mockResolvedValue(undefined),
    loginRedirect: vi.fn().mockResolvedValue(undefined),
  },
}));

describe('App', () => {
  it('renders without crashing when MSAL is in authenticated state (mock)', () => {
    mocks.showAuthenticated.value = true;
    render(<App />);
    expect(screen.getByText('AgentHub')).toBeInTheDocument();
  });

  it('renders login redirect when MSAL is not authenticated', () => {
    mocks.showAuthenticated.value = false;
    render(<App />);
    expect(screen.getByRole('button', { name: /sign in/i })).toBeInTheDocument();
  });
});
