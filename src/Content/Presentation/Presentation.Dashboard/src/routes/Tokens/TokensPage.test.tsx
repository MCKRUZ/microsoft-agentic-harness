import { screen, waitFor } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import { renderPage } from '@/test/helpers/renderPage';
import TokensPage from './TokensPage';

describe('TokensPage', () => {
  it('renders all 4 KPI cards with data', async () => {
    renderPage(<TokensPage />);

    const kpis = await screen.findAllByRole('status', {}, { timeout: 3000 });
    expect(kpis.length).toBeGreaterThanOrEqual(4);

    for (const title of ['Input Tokens', 'Output Tokens', 'Cache Read', 'Cache Write']) {
      expect(screen.getByLabelText(title)).toBeInTheDocument();
    }
  });

  it('renders chart panels for rates and distribution', async () => {
    renderPage(<TokensPage />);

    await screen.findAllByRole('status', {}, { timeout: 3000 });

    expect(screen.getByText('Input Token Rate')).toBeInTheDocument();
    expect(screen.getByText('Output Token Rate')).toBeInTheDocument();
    expect(screen.getByText('Distribution by Model')).toBeInTheDocument();
    expect(screen.getByText('Cache Hit Rate')).toBeInTheDocument();
  });

  it('KPI values show formatted token counts', async () => {
    renderPage(<TokensPage />);

    const kpis = await screen.findAllByRole('status', {}, { timeout: 3000 });
    for (const kpi of kpis) {
      const valueEl = kpi.querySelector('.text-2xl');
      expect(valueEl).toBeTruthy();
      expect(valueEl!.textContent).not.toBe('');
    }
  });
});
