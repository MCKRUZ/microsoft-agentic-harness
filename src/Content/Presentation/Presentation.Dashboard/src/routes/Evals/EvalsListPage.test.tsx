import { describe, it, expect, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router';
import EvalsListPage from './EvalsListPage';

// The SignalR live hook tries to open a connection during render; stub it out
// so tests don't depend on a hub being up. The hook is covered by its own
// integration boundary (server-side SignalREvalRunNotifierTests pin the wire
// contract); rendering tests just need it to be a no-op.
vi.mock('@/hooks/useEvalRunLive', () => ({
  useEvalRunLive: () => undefined,
}));

function renderWith() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0, staleTime: 0 } },
  });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter>
        <EvalsListPage />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('EvalsListPage', () => {
  it('renders the run row from the mocked API', async () => {
    renderWith();
    await waitFor(() => expect(screen.getByText('run-001')).toBeInTheDocument());
    // Verdict badge is a <span> inside the row; the table header is a <th>
    // with the same text. The badge has the verdict-specific CSS classes.
    const verdictBadge = screen
      .getAllByText('Fail')
      .find((el) => el.tagName === 'SPAN');
    expect(verdictBadge).toBeDefined();
    expect(screen.getByText('80.0%')).toBeInTheDocument();
  });

  it('renders the empty-state when no runs are returned', async () => {
    const { server } = await import('@/test/mocks/server');
    const { http, HttpResponse } = await import('msw');
    server.use(http.get('/api/evals/runs', () => HttpResponse.json([])));

    renderWith();

    await waitFor(() => expect(screen.getByText(/no runs ingested/i)).toBeInTheDocument());
  });
});
