import { useMsal } from '@azure/msal-react';
import { InteractionRequiredAuthError, type AccountInfo } from '@azure/msal-browser';
import { loginRequest } from '@/lib/authConfig';

export interface UseAuthReturn {
  account: AccountInfo | null;
  isAuthenticated: boolean;
  acquireToken: () => Promise<string>;
  signOut: () => void;
}

export function useAuth(): UseAuthReturn {
  const { instance, accounts } = useMsal();
  const account = accounts[0] ?? null;

  const acquireToken = async (): Promise<string> => {
    if (!account) throw new Error('No account available');
    try {
      const result = await instance.acquireTokenSilent({ account, scopes: loginRequest.scopes });
      return result.accessToken;
    } catch (error) {
      if (error instanceof InteractionRequiredAuthError) {
        const result = await instance.acquireTokenPopup({ account, scopes: loginRequest.scopes });
        return result.accessToken;
      }
      throw error;
    }
  };

  const signOut = (): void => {
    void instance.logoutRedirect();
  };

  return { account, isAuthenticated: account !== null, acquireToken, signOut };
}
