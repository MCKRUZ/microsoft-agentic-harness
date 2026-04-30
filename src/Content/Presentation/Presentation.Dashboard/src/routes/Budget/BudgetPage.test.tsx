import { screen } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import { renderPage } from '@/test/helpers/renderPage';
import BudgetPage from './BudgetPage';

describe('BudgetPage', () => {
  it('renders KPI cards with budget data', async () => {
    renderPage(<BudgetPage />);

    const kpis = await screen.findAllByRole('status', {}, { timeout: 3000 });
    expect(kpis.length).toBeGreaterThanOrEqual(3);

    expect(screen.getByLabelText('Total Spent')).toBeInTheDocument();
    expect(screen.getByLabelText('Budget Limit')).toBeInTheDocument();
    expect(screen.getByLabelText('Remaining')).toBeInTheDocument();
  });

  it('renders chart panels for budget analysis', async () => {
    renderPage(<BudgetPage />);

    await screen.findAllByRole('status', {}, { timeout: 3000 });

    expect(screen.getByText('Spend Rate')).toBeInTheDocument();
    expect(screen.getByText('Budget Utilization')).toBeInTheDocument();
    expect(screen.getByText('Spend Gauge')).toBeInTheDocument();
  });

  it('KPI values show USD formatting', async () => {
    renderPage(<BudgetPage />);

    const spentCard = await screen.findByLabelText('Total Spent', {}, { timeout: 3000 });
    const valueEl = spentCard.querySelector('.text-2xl');
    expect(valueEl).toBeTruthy();
    expect(valueEl!.textContent).toContain('$');
  });

  it('budget status indicator shows OK/WARNING/CRITICAL', async () => {
    renderPage(<BudgetPage />);

    await screen.findAllByRole('status', {}, { timeout: 3000 });

    expect(screen.getByText('Budget Status')).toBeInTheDocument();
    expect(screen.getByText('OK')).toBeInTheDocument();
  });
});
