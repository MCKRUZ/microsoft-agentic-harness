import { useEffect } from 'react';
import { useMsal } from '@azure/msal-react';
import { Providers } from './providers';
import { AppRouter } from './router';
import { setMsalInstance } from '@/lib/apiClient';

function MsalInstanceSync() {
  const { instance } = useMsal();
  useEffect(() => { setMsalInstance(instance); }, [instance]);
  return null;
}

export default function App() {
  return (
    <Providers>
      <MsalInstanceSync />
      <AppRouter />
    </Providers>
  );
}
