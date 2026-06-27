import { screen, waitFor } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import { renderPage } from '@/test/helpers/renderPage';
import OverviewPage from './OverviewPage';

describe('OverviewPage', () => {
  it('renders all 6 KPI cards with data', async () => {
    renderPage(<OverviewPage />);

    const kpis = await screen.findAllByRole('status', {}, { timeout: 3000 });
    expect(kpis.length).toBeGreaterThanOrEqual(6);

    for (const title of ['Tokens / Minute', 'Active Sessions', 'Cost Today', 'Cache Hit Rate', 'Safety Evaluations', 'Budget Status']) {
      expect(screen.getByLabelText(title)).toBeInTheDocument();
    }
  });

  it('renders chart panels', async () => {
    renderPage(<OverviewPage />);

    await screen.findAllByRole('status', {}, { timeout: 3000 });

    expect(screen.getByText('Token Throughput')).toBeInTheDocument();
    expect(screen.getByText('Cost Rate')).toBeInTheDocument();
    expect(screen.getByText('Cache Efficiency')).toBeInTheDocument();
    expect(screen.getByText('Budget Utilization')).toBeInTheDocument();
  });

  it('KPI values are non-empty', async () => {
    renderPage(<OverviewPage />);

    const kpis = await screen.findAllByRole('status', {}, { timeout: 3000 });
    for (const kpi of kpis) {
      const valueEl = kpi.querySelector('.text-2xl');
      expect(valueEl).toBeTruthy();
      expect(valueEl!.textContent).not.toBe('');
    }
  });
});
