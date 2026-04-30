import { screen, waitFor } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import { renderPage } from '@/test/helpers/renderPage';
import SessionsPage from './SessionsPage';

describe('SessionsPage', () => {
  it('renders KPI cards for session metrics', async () => {
    renderPage(<SessionsPage />);

    const kpis = await screen.findAllByRole('status', {}, { timeout: 3000 });
    expect(kpis.length).toBeGreaterThanOrEqual(4);

    expect(screen.getByLabelText('Total Sessions')).toBeInTheDocument();
    expect(screen.getByLabelText('Active Sessions')).toBeInTheDocument();
    expect(screen.getByLabelText('Avg Turns/Session')).toBeInTheDocument();
    expect(screen.getByLabelText('Avg Duration')).toBeInTheDocument();
  });

  it('renders session table with data rows', async () => {
    renderPage(<SessionsPage />);

    await waitFor(
      () => {
        expect(screen.getAllByText('CodeAssistant').length).toBeGreaterThanOrEqual(1);
      },
      { timeout: 3000 },
    );

    expect(screen.getByText('ResearchAgent')).toBeInTheDocument();
  });

  it('displays agent names and models in the table', async () => {
    renderPage(<SessionsPage />);

    await waitFor(
      () => {
        expect(screen.getAllByText('CodeAssistant').length).toBeGreaterThanOrEqual(1);
      },
      { timeout: 3000 },
    );

    expect(screen.getAllByText('claude-3-opus').length).toBeGreaterThanOrEqual(1);
    expect(screen.getAllByText('claude-3-sonnet').length).toBeGreaterThanOrEqual(1);
  });

  it('renders Recent Sessions panel', async () => {
    renderPage(<SessionsPage />);

    await screen.findAllByRole('status', {}, { timeout: 3000 });

    expect(screen.getByText('Recent Sessions')).toBeInTheDocument();
  });
});
