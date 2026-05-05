import { describe, it, expect, vi } from 'vitest';
import { screen } from '@testing-library/react';
import { Header } from '@/components/layout/Header';
import { renderWithProviders } from '@/test/utils';

vi.mock('@/lib/authConfig', () => ({
  loginRequest: { scopes: ['api://test-api/access_as_user'] },
}));

vi.mock('@azure/msal-react', () => ({
  useMsal: () => ({
    instance: { logoutRedirect: vi.fn().mockResolvedValue(undefined) },
    accounts: [{ name: 'Test User', username: 'test@test.com' }],
    inProgress: 'none',
  }),
}));

vi.mock('@/hooks/useAgentHub', () => ({
  useAgentHub: () => ({
    connectionState: 'connected' as const,
    startConversation: vi.fn().mockResolvedValue(undefined),
    invokeToolViaAgent: vi.fn().mockResolvedValue(undefined),
    retryFromMessage: vi.fn().mockResolvedValue(undefined),
    editAndResubmit: vi.fn().mockResolvedValue(undefined),
    setConversationSettings: vi.fn().mockResolvedValue(undefined),
  }),
  AgentHubProvider: ({ children }: { children: React.ReactNode }) => <>{children}</>,
}));

vi.mock('@/lib/devAuth', () => ({
  IS_AUTH_DISABLED: false,
}));

describe('Header', () => {
  it('renders app name', () => {
    renderWithProviders(<Header />);
    expect(screen.getByText('AgentHub')).toBeInTheDocument();
  });
});
