import { screen } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import { renderPage } from '@/test/helpers/renderPage';
import RagPage from './RagPage';

describe('RagPage', () => {
  it('renders all 4 KPI cards with data', async () => {
    renderPage(<RagPage />);

    const kpis = await screen.findAllByRole('status', {}, { timeout: 3000 });
    expect(kpis.length).toBeGreaterThanOrEqual(4);

    expect(screen.getByLabelText('Documents Ingested')).toBeInTheDocument();
    expect(screen.getByLabelText('Retrievals')).toBeInTheDocument();
    expect(screen.getByLabelText('Avg Latency')).toBeInTheDocument();
    expect(screen.getByLabelText('Avg Chunks')).toBeInTheDocument();
  });

  it('renders chart panels for RAG analytics', async () => {
    renderPage(<RagPage />);

    await screen.findAllByRole('status', {}, { timeout: 3000 });

    expect(screen.getByText('Ingestion Throughput')).toBeInTheDocument();
    expect(screen.getByText('Retrieval Latency')).toBeInTheDocument();
    expect(screen.getByText('Top Sources')).toBeInTheDocument();
  });

  it('KPI values are non-empty', async () => {
    renderPage(<RagPage />);

    const kpis = await screen.findAllByRole('status', {}, { timeout: 3000 });
    for (const kpi of kpis) {
      const valueEl = kpi.querySelector('.text-2xl');
      expect(valueEl).toBeTruthy();
      expect(valueEl!.textContent).not.toBe('');
    }
  });
});
