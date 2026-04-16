import { renderHook, act } from '@testing-library/react';
import { vi, describe, it, expect, beforeEach } from 'vitest';
import { InteractionRequiredAuthError } from '@azure/msal-browser';
import { useAuth } from '../useAuth';

const mocks = vi.hoisted(() => ({
  acquireTokenSilent: vi.fn(),
  acquireTokenPopup: vi.fn(),
  logoutRedirect: vi.fn(),
}));

vi.mock('@azure/msal-react', () => ({
  useMsal: () => ({
    instance: {
      acquireTokenSilent: mocks.acquireTokenSilent,
      acquireTokenPopup: mocks.acquireTokenPopup,
      logoutRedirect: mocks.logoutRedirect,
    },
    accounts: [{
      username: 'test@example.com',
      homeAccountId: '1',
      environment: 'login.microsoftonline.com',
      tenantId: 'tid',
      localAccountId: 'lid',
    }],
  }),
}));

vi.mock('@/lib/authConfig', () => ({
  loginRequest: { scopes: ['api://test-api/access_as_user'] },
}));

describe('useAuth', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('acquireToken returns token from acquireTokenSilent', async () => {
    mocks.acquireTokenSilent.mockResolvedValue({ accessToken: 'silent-token' });

    const { result } = renderHook(() => useAuth());

    let token!: string;
    await act(async () => {
      token = await result.current.acquireToken();
    });

    expect(token).toBe('silent-token');
    expect(mocks.acquireTokenSilent).toHaveBeenCalledOnce();
    expect(mocks.acquireTokenPopup).not.toHaveBeenCalled();
  });

  it('acquireToken falls back to acquireTokenPopup on InteractionRequiredAuthError', async () => {
    mocks.acquireTokenSilent.mockRejectedValue(
      new InteractionRequiredAuthError('interaction_required'),
    );
    mocks.acquireTokenPopup.mockResolvedValue({ accessToken: 'popup-token' });

    const { result } = renderHook(() => useAuth());

    let token!: string;
    await act(async () => {
      token = await result.current.acquireToken();
    });

    expect(token).toBe('popup-token');
    expect(mocks.acquireTokenPopup).toHaveBeenCalledOnce();
  });
});
