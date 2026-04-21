import { MsalProvider } from '@azure/msal-react';
import { QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { msalInstance } from '@/lib/authConfig';
import { IS_AUTH_DISABLED } from '@/lib/devAuth';
import { queryClient } from '@/lib/queryClient';
import { ThemeProvider } from '@/components/theme/ThemeProvider';
import { TooltipProvider } from '@/components/ui/tooltip';
import { AgentHubProvider } from '@/hooks/useAgentHub';

interface ProvidersProps {
  children: ReactNode;
}

export function Providers({ children }: ProvidersProps) {
  const inner = (
    <QueryClientProvider client={queryClient}>
      <ThemeProvider>
        <TooltipProvider>
          <AgentHubProvider>
            {children}
          </AgentHubProvider>
        </TooltipProvider>
      </ThemeProvider>
    </QueryClientProvider>
  );

  if (IS_AUTH_DISABLED) return inner;

  return <MsalProvider instance={msalInstance}>{inner}</MsalProvider>;
}
