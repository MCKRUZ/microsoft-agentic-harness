import { MsalProvider } from '@azure/msal-react';
import { QueryClientProvider } from '@tanstack/react-query';
// 'react-router/dom', not 'react-router'. Both entries export a RouterProvider; the DOM one wraps
// the other with `flushSync: ReactDOM.flushSync`. The plain import type-checks, renders, and passes
// every test — it just loses the synchronous flush. Do not "normalise" this to match the imports in
// the rest of the app.
import { RouterProvider } from 'react-router/dom';
import { msalInstance } from '@/auth/authConfig';
import { setMsalInstance } from '@/api/client';
import { queryClient } from './queryClient';
import { router } from './router';

setMsalInstance(msalInstance);

export default function App() {
  return (
    <MsalProvider instance={msalInstance}>
      <QueryClientProvider client={queryClient}>
        <RouterProvider router={router} />
      </QueryClientProvider>
    </MsalProvider>
  );
}
