import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { http, HttpResponse } from 'msw';
import { server } from '@/test/mocks/server';

/**
 * Regression coverage for the solution-review finding "401 response interceptor lacks
 * the IS_AUTH_DISABLED guard". When auth is disabled the Dashboard's msal instance is
 * built from a placeholder { clientId: 'dev', authority: '.../dev' } config, so firing
 * loginRedirect on a 401 navigates the operator to a bogus AAD error page instead of
 * surfacing the misconfiguration. The response interceptor must short-circuit when
 * IS_AUTH_DISABLED is true, exactly like the request interceptor already does.
 *
 * authConfig is stubbed so the client under test does not depend on real MSAL env vars
 * (VITE_AZURE_SPA_CLIENT_ID etc.); the only behavior exercised here is the interceptor's
 * IS_AUTH_DISABLED guard around loginRedirect.
 */

const ENDPOINT = 'http://test.local/protected';

/**
 * Builds a fresh apiClient module with IS_AUTH_DISABLED mocked to the supplied value.
 * A fresh module is required per case because the interceptor's _redirecting flag and
 * the injected msal instance are module-level state.
 */
async function loadClientWithAuthDisabled(authDisabled: boolean) {
  vi.resetModules();
  vi.doMock('@/auth/devAuth', () => ({ IS_AUTH_DISABLED: authDisabled }));
  vi.doMock('@/auth/authConfig', () => ({ loginRequest: { scopes: [] } }));
  const loginRedirect = vi.fn().mockResolvedValue(undefined);
  const { apiClient, setMsalInstance } = await import('./client');
  // getAllAccounts returns [] so the request interceptor short-circuits (no account)
  // and the request reaches the 401-producing handler regardless of IS_AUTH_DISABLED.
  setMsalInstance({ loginRedirect, getAllAccounts: () => [] } as never);
  return { apiClient, loginRedirect };
}

describe('apiClient 401 response interceptor — IS_AUTH_DISABLED guard', () => {
  beforeEach(() => {
    server.use(http.get(ENDPOINT, () => new HttpResponse(null, { status: 401 })));
  });

  afterEach(() => {
    vi.doUnmock('@/auth/devAuth');
    vi.doUnmock('@/auth/authConfig');
    vi.resetModules();
  });

  it('does not trigger loginRedirect on 401 when auth is disabled', async () => {
    const { apiClient, loginRedirect } = await loadClientWithAuthDisabled(true);

    await expect(apiClient.get(ENDPOINT)).rejects.toMatchObject({
      response: { status: 401 },
    });

    expect(loginRedirect).not.toHaveBeenCalled();
  });

  it('triggers loginRedirect on 401 when auth is enabled', async () => {
    const { apiClient, loginRedirect } = await loadClientWithAuthDisabled(false);

    await expect(apiClient.get(ENDPOINT)).rejects.toMatchObject({
      response: { status: 401 },
    });

    expect(loginRedirect).toHaveBeenCalledTimes(1);
  });
});
