import axios from 'axios';
import { vi, describe, it, expect, beforeEach } from 'vitest';
import type { IPublicClientApplication } from '@azure/msal-browser';
import { apiClient, setMsalInstance } from '../apiClient';

const mocks = vi.hoisted(() => ({
  acquireTokenSilent: vi.fn(),
  loginRedirect: vi.fn(),
  getAllAccounts: vi.fn(),
}));

vi.mock('@/lib/authConfig', () => ({
  loginRequest: { scopes: ['api://test-api/access_as_user'] },
}));

const mockAccount = {
  username: 'test@example.com',
  homeAccountId: '1',
  environment: 'login.microsoftonline.com',
  tenantId: 'tid',
  localAccountId: 'lid',
};

const mockMsalInstance = {
  getAllAccounts: mocks.getAllAccounts,
  acquireTokenSilent: mocks.acquireTokenSilent,
  loginRedirect: mocks.loginRedirect,
} as unknown as IPublicClientApplication;

describe('apiClient', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mocks.getAllAccounts.mockReturnValue([mockAccount]);
    setMsalInstance(mockMsalInstance);
  });

  it('attaches Authorization Bearer header to requests', async () => {
    mocks.acquireTokenSilent.mockResolvedValue({ accessToken: 'test-token' });

    let capturedAuth: string | null = null;

    await apiClient.get('/test', {
      adapter: async (config) => {
        capturedAuth = config.headers.get('Authorization') as string | null;
        return { data: {}, status: 200, statusText: 'OK', headers: {}, config };
      },
    });

    expect(capturedAuth).toBe('Bearer test-token');
  });

  it('redirects to login on 401 response', async () => {
    mocks.acquireTokenSilent.mockResolvedValue({ accessToken: 'test-token' });
    mocks.loginRedirect.mockResolvedValue(undefined);

    const error = new axios.AxiosError('Unauthorized', 'ERR_BAD_REQUEST');
    error.response = {
      status: 401,
      data: {},
      statusText: 'Unauthorized',
      headers: {},
      config: { headers: new axios.AxiosHeaders() },
    };

    await expect(
      apiClient.get('/test', {
        adapter: () => Promise.reject(error),
      }),
    ).rejects.toThrow();

    expect(mocks.loginRedirect).toHaveBeenCalledOnce();
  });
});
