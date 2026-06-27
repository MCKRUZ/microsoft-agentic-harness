import { render, type RenderOptions } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import type { ReactElement, ReactNode } from 'react';
import { TooltipProvider } from '@/components/ui/tooltip';

function createTestQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  });
}

interface RenderWithProvidersOptions extends Omit<RenderOptions, 'wrapper'> {
  route?: string;
}

/**
 * Renders a component inside the providers the chat surface needs at test time:
 * an in-memory router, a no-retry QueryClient, and the tooltip provider. Theme
 * is applied imperatively via the Dashboard's `themeStore` (no context
 * provider), so none is wrapped here.
 */
export function renderWithProviders(
  ui: ReactElement,
  { route = '/', ...renderOptions }: RenderWithProvidersOptions = {},
) {
  const testQueryClient = createTestQueryClient();

  function Wrapper({ children }: { children: ReactNode }) {
    return (
      <MemoryRouter initialEntries={[route]}>
        <QueryClientProvider client={testQueryClient}>
          <TooltipProvider>{children}</TooltipProvider>
        </QueryClientProvider>
      </MemoryRouter>
    );
  }

  return render(ui, { wrapper: Wrapper, ...renderOptions });
}
