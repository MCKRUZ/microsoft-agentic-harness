import { screen } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import { renderPage } from '@/test/helpers/renderPage';
import CostPage from './CostPage';

describe('CostPage', () => {
  it('renders KPI cards with cost data', async () => {
    renderPage(<CostPage />);

    const kpis = await screen.findAllByRole('status', {}, { timeout: 3000 });
    expect(kpis.length).toBeGreaterThanOrEqual(3);

    expect(screen.getByLabelText('Total Cost')).toBeInTheDocument();
    expect(screen.getByLabelText('Cache Savings')).toBeInTheDocument();
    expect(screen.getByLabelText('Budget Remaining')).toBeInTheDocument();
  });

  it('renders chart panels for cost analysis', async () => {
    renderPage(<CostPage />);

    await screen.findAllByRole('status', {}, { timeout: 3000 });

    expect(screen.getByText('Cost Rate')).toBeInTheDocument();
    expect(screen.getByText('Cost by Model')).toBeInTheDocument();
    expect(screen.getByText('Budget Progress')).toBeInTheDocument();
    expect(screen.getByText('Cache ROI')).toBeInTheDocument();
  });

  it('KPI values display USD formatting', async () => {
    renderPage(<CostPage />);

    const costCard = await screen.findByLabelText('Total Cost', {}, { timeout: 3000 });
    const valueEl = costCard.querySelector('.text-2xl');
    expect(valueEl).toBeTruthy();
    expect(valueEl!.textContent).toContain('$');
  });
});
