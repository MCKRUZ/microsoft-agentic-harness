import { describe, it, expect } from 'vitest';
import { screen, waitFor } from '@testing-library/react';
import { server } from '@/test/mocks/server';
import { http, HttpResponse } from 'msw';
import { renderRoutedPage } from '@/test/helpers/renderPage';
import MessageBodyPage from './MessageBodyPage';

const sessionId = '11111111-1111-1111-1111-111111111111';
const messageId = '33333333-3333-3333-3333-333333333333';

describe('MessageBodyPage', () => {
  it('renders the full message body and back link', async () => {
    renderRoutedPage(MessageBodyPage, {
      route: `/sessions/${sessionId}/files/${messageId}`,
      path: '/sessions/:sessionId/files/:messageId',
    });

    await waitFor(
      () => {
        expect(screen.getByText(/Turn 2/)).toBeInTheDocument();
      },
      { timeout: 3000 },
    );

    const body = screen.getByTestId('message-body-content');
    expect(body.textContent).toContain('What does App.tsx do? Be detailed');

    const backLink = screen.getByRole('link', { name: /Back to session/ });
    expect(backLink).toHaveAttribute('href', `/sessions/${sessionId}`);
  });

  it('shows a fallback notice when contentFull is null', async () => {
    // Override the default handler to simulate a row that predates the
    // content_full schema migration.
    server.use(
      http.get('/api/sessions/:id/messages/:messageId', ({ params }) =>
        HttpResponse.json({
          id: params['messageId'],
          sessionId: params['id'],
          turnIndex: 1,
          role: 'user',
          source: 'user_message',
          contentPreview: 'just the preview',
          contentFull: null,
          model: null,
          createdAt: '2026-06-02T14:00:00Z',
        }),
      ),
    );

    renderRoutedPage(MessageBodyPage, {
      route: `/sessions/${sessionId}/files/${messageId}`,
      path: '/sessions/:sessionId/files/:messageId',
    });

    await waitFor(
      () => {
        expect(
          screen.getByText(/predates the/),
        ).toBeInTheDocument();
      },
      { timeout: 3000 },
    );

    const body = screen.getByTestId('message-body-content');
    expect(body.textContent).toContain('just the preview');
  });

  it('renders not-found state for a missing message', async () => {
    renderRoutedPage(MessageBodyPage, {
      route: `/sessions/${sessionId}/files/00000000-0000-0000-0000-000000000404`,
      path: '/sessions/:sessionId/files/:messageId',
    });

    await waitFor(
      () => {
        expect(
          screen.getByText(/Failed to load message|Message not found/),
        ).toBeInTheDocument();
      },
      { timeout: 3000 },
    );
  });
});
