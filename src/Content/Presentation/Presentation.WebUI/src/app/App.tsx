import { useEffect } from 'react';
import { useMsal } from '@azure/msal-react';
import { Providers } from './providers';
import { AppRouter } from './router';
import { setMsalInstance } from '@/lib/apiClient';
import { IS_AUTH_DISABLED } from '@/lib/devAuth';

function MsalInstanceSync() {
  const { instance } = useMsal();
  useEffect(() => { setMsalInstance(instance); }, [instance]);
  return null;
}

export default function App() {
  return (
    <Providers>
      {!IS_AUTH_DISABLED && <MsalInstanceSync />}
      <AppRouter />
    </Providers>
  );
}
