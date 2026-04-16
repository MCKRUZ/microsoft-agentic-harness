import { useMsal } from '@azure/msal-react';
import { InteractionRequiredAuthError, type AccountInfo } from '@azure/msal-browser';
import { loginRequest } from '@/lib/authConfig';
import { IS_AUTH_DISABLED, DEV_ACCOUNT } from '@/lib/devAuth';

export interface UseAuthReturn {
  account: AccountInfo | null;
  isAuthenticated: boolean;
  acquireToken: () => Promise<string>;
  signOut: () => void;
}

function useMsalAuth(): UseAuthReturn {
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

  return {
    account,
    isAuthenticated: account !== null,
    acquireToken,
    signOut: () => { void instance.logoutRedirect(); },
  };
}

function useDevAuth(): UseAuthReturn {
  return {
    account: DEV_ACCOUNT,
    isAuthenticated: true,
    acquireToken: () => Promise.resolve('dev-token'),
    signOut: () => {},
  };
}

// Resolved once at module load — IS_AUTH_DISABLED is a build-time constant,
// so the chosen hook is always the same across all renders (no conditional hook violation).
export const useAuth = IS_AUTH_DISABLED ? useDevAuth : useMsalAuth;
