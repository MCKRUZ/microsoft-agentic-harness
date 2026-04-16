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

describe('Header', () => {
  it('renders app name', () => {
    renderWithProviders(<Header />);
    expect(screen.getByText('AgentHub')).toBeInTheDocument();
  });
});
