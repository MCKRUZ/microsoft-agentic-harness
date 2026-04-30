import { screen, waitFor } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import { renderRoutedPage } from '@/test/helpers/renderPage';
import SessionDetailPage from './SessionDetailPage';

const testSessionId = '11111111-1111-1111-1111-111111111111';

describe('SessionDetailPage', () => {
  it('renders session header with agent info', async () => {
    renderRoutedPage(SessionDetailPage, {
      route: `/sessions/${testSessionId}`,
      path: '/sessions/:sessionId',
    });

    await waitFor(
      () => {
        expect(screen.getByText('CodeAssistant')).toBeInTheDocument();
      },
      { timeout: 3000 },
    );

    expect(screen.getByText('claude-3-opus')).toBeInTheDocument();
  });

  it('shows conversation messages', async () => {
    renderRoutedPage(SessionDetailPage, {
      route: `/sessions/${testSessionId}`,
      path: '/sessions/:sessionId',
    });

    await waitFor(
      () => {
        expect(screen.getByText(/refactor the authentication module/i)).toBeInTheDocument();
      },
      { timeout: 3000 },
    );
  });

  it('displays tool execution records', async () => {
    renderRoutedPage(SessionDetailPage, {
      route: `/sessions/${testSessionId}`,
      path: '/sessions/:sessionId',
    });

    await waitFor(
      () => {
        const fileSearchEls = screen.getAllByText('file_search');
        expect(fileSearchEls.length).toBeGreaterThanOrEqual(1);
      },
      { timeout: 3000 },
    );

    const codeExecEls = screen.getAllByText('code_exec');
    expect(codeExecEls.length).toBeGreaterThanOrEqual(1);
  });

  it('renders Tool Executions panel', async () => {
    renderRoutedPage(SessionDetailPage, {
      route: `/sessions/${testSessionId}`,
      path: '/sessions/:sessionId',
    });

    await waitFor(
      () => {
        expect(screen.getByText('Tool Executions')).toBeInTheDocument();
      },
      { timeout: 3000 },
    );
  });
});
