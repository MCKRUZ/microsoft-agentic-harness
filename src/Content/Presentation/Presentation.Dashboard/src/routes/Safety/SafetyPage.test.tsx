import { screen } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import { renderPage } from '@/test/helpers/renderPage';
import SafetyPage from './SafetyPage';

describe('SafetyPage', () => {
  it('renders KPI cards for safety metrics', async () => {
    renderPage(<SafetyPage />);

    const kpis = await screen.findAllByRole('status', {}, { timeout: 3000 });
    expect(kpis.length).toBeGreaterThanOrEqual(3);

    expect(screen.getByLabelText('Total Violations')).toBeInTheDocument();
    expect(screen.getByLabelText('Blocked Requests')).toBeInTheDocument();
    expect(screen.getByLabelText('Safety Checks')).toBeInTheDocument();
  });

  it('renders chart panels for safety analysis', async () => {
    renderPage(<SafetyPage />);

    await screen.findAllByRole('status', {}, { timeout: 3000 });

    expect(screen.getByText('Violation Trend')).toBeInTheDocument();
    expect(screen.getByText('Violations by Category')).toBeInTheDocument();
    expect(screen.getByText('Block Rate')).toBeInTheDocument();
  });

  it('KPI values are non-empty', async () => {
    renderPage(<SafetyPage />);

    const kpis = await screen.findAllByRole('status', {}, { timeout: 3000 });
    for (const kpi of kpis) {
      const valueEl = kpi.querySelector('.text-2xl');
      expect(valueEl).toBeTruthy();
      expect(valueEl!.textContent).not.toBe('');
    }
  });
});
