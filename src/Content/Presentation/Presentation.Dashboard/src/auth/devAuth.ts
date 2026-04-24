import type { AccountInfo } from '@azure/msal-browser'

export const IS_AUTH_DISABLED = !import.meta.env['VITE_AZURE_SPA_CLIENT_ID']

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
