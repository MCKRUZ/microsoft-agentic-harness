import { MsalProvider } from '@azure/msal-react';
import { QueryClientProvider } from '@tanstack/react-query';
import { RouterProvider } from 'react-router-dom';
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
