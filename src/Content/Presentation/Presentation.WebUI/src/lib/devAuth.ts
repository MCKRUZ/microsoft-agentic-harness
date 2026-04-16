import type { AccountInfo } from '@azure/msal-browser'

/** True when VITE_AUTH_DISABLED=true — skips MSAL entirely for local dev without Azure AD. */
export const IS_AUTH_DISABLED = import.meta.env['VITE_AUTH_DISABLED'] === 'true'

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
