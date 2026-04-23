import type { AccountInfo } from '@azure/msal-browser'

/**
 * Auth is disabled when Azure AD credentials are not configured.
 * No env file required for the default (auth-off) dev experience — the presence
 * of VITE_AZURE_SPA_CLIENT_ID is the sole signal. This eliminates the recurring
 * "Disconnected" bug caused by gitignored .env files vanishing on clone/reset.
 */
export const IS_AUTH_DISABLED = !import.meta.env['VITE_AZURE_SPA_CLIENT_ID']

/** Synthetic account returned by useAuth when auth is disabled. */
export const DEV_ACCOUNT: AccountInfo = {
  homeAccountId: 'dev-user',
  localAccountId: 'dev-user',
  environment: 'dev',
  tenantId: 'dev-tenant',
  username: 'dev@localhost',
  name: 'Dev User',
  idToken: undefined,
  idTokenClaims: { oid: 'dev-user', name: 'Dev User' },
  nativeAccountId: undefined,
  authorityType: 'MSSTS',
}
