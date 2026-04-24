import type { Configuration, PopupRequest } from '@azure/msal-browser';
import { PublicClientApplication } from '@azure/msal-browser';
import { IS_AUTH_DISABLED } from './devAuth';

function requireEnv(key: string): string {
  const value = (import.meta.env as Record<string, string | undefined>)[key];
  if (!value) throw new Error(`Missing required environment variable: ${key}`);
  return value;
}

export const msalConfig: Configuration = IS_AUTH_DISABLED
  ? { auth: { clientId: 'dev', authority: 'https://login.microsoftonline.com/dev' } }
  : {
      auth: {
        clientId: requireEnv('VITE_AZURE_SPA_CLIENT_ID'),
        authority: `https://login.microsoftonline.com/${requireEnv('VITE_AZURE_TENANT_ID')}`,
        redirectUri: window.location.origin,
      },
      cache: { cacheLocation: 'sessionStorage' },
    };

export const loginRequest: PopupRequest = {
  scopes: IS_AUTH_DISABLED
    ? []
    : [`api://${requireEnv('VITE_AZURE_API_CLIENT_ID')}/access_as_user`],
};

export const msalInstance = new PublicClientApplication(msalConfig);
