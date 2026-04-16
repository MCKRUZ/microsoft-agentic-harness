import axios from 'axios';
import { InteractionRequiredAuthError, type IPublicClientApplication } from '@azure/msal-browser';
import { loginRequest } from './authConfig';

let _msalInstance: IPublicClientApplication | null = null;
let _redirecting = false;

export function setMsalInstance(instance: IPublicClientApplication): void {
  _msalInstance = instance;
}

export const apiClient = axios.create({
  baseURL: import.meta.env['VITE_API_BASE_URL'] as string | undefined,
});

apiClient.interceptors.request.use(async (config) => {
  if (!_msalInstance) return config;

  const account = _msalInstance.getAllAccounts()[0];
  if (!account) return config;

  try {
    const result = await _msalInstance.acquireTokenSilent({ account, scopes: loginRequest.scopes });
    config.headers.set('Authorization', `Bearer ${result.accessToken}`);
  } catch (error) {
    if (error instanceof InteractionRequiredAuthError) {
      const result = await _msalInstance.acquireTokenPopup({ account, scopes: loginRequest.scopes });
      config.headers.set('Authorization', `Bearer ${result.accessToken}`);
    } else {
      throw error;
    }
  }

  return config;
});

apiClient.interceptors.response.use(
  (response) => response,
  async (error: unknown) => {
    const axiosError = axios.isAxiosError(error) ? error : null;
    if (axiosError?.response?.status === 401 && !_redirecting) {
      _redirecting = true;
      await _msalInstance?.loginRedirect(loginRequest);
    }
    return Promise.reject(error);
  },
);
