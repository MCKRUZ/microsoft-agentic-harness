import type { Configuration, PopupRequest } from '@azure/msal-browser';
import { PublicClientApplication } from '@azure/msal-browser';

export const msalConfig: Configuration = {
  auth: {
    clientId: (import.meta.env['VITE_AZURE_CLIENT_ID'] as string | undefined) ?? '',
    authority: `https://login.microsoftonline.com/${(import.meta.env['VITE_AZURE_TENANT_ID'] as string | undefined) ?? 'common'}`,
    redirectUri: window.location.origin,
  },
  cache: {
    cacheLocation: 'sessionStorage',
  },
};

export const loginRequest: PopupRequest = {
  scopes: [`api://${(import.meta.env['VITE_AZURE_CLIENT_ID'] as string | undefined) ?? ''}/.default`],
};

export const msalInstance = new PublicClientApplication(msalConfig);
