import { screen } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import { http, HttpResponse } from 'msw';
import { server } from '@/test/mocks/server';
import { renderPage } from '@/test/helpers/renderPage';
import OverviewPage from './OverviewPage';
import type { MetricsQueryResponse } from '@/api/types';

const emptyResponse: MetricsQueryResponse = {
  success: true,
  resultType: 'matrix',
  series: [],
};

const errorResponse: MetricsQueryResponse = {
  success: false,
  resultType: 'error',
  series: [],
  error: 'metric not found',
};

describe('OverviewPage data edge cases', () => {
  it('renders with zero values when Prometheus returns empty series', async () => {
    server.use(
      http.get('/api/metrics/range', () => HttpResponse.json(emptyResponse)),
    );

    renderPage(<OverviewPage />);

    const kpis = await screen.findAllByRole('status', {}, { timeout: 3000 });
    expect(kpis.length).toBeGreaterThanOrEqual(6);

    for (const kpi of kpis) {
      const valueEl = kpi.querySelector('.text-2xl');
      expect(valueEl).toBeTruthy();
    }
  });

  it('renders gracefully when API returns error response', async () => {
    server.use(
      http.get('/api/metrics/range', () => HttpResponse.json(errorResponse)),
    );

    renderPage(<OverviewPage />);

    const kpis = await screen.findAllByRole('status', {}, { timeout: 3000 });
    expect(kpis.length).toBeGreaterThanOrEqual(6);
  });

  it('renders gracefully when API returns 500', async () => {
    server.use(
      http.get('/api/metrics/range', () => new HttpResponse(null, { status: 500 })),
    );

    renderPage(<OverviewPage />);

    await expect(screen.findByText('Overview', {}, { timeout: 3000 })).resolves.toBeInTheDocument();
  });
});
