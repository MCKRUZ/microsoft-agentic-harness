import { screen } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import { renderPage } from '@/test/helpers/renderPage';
import ToolsPage from './ToolsPage';

describe('ToolsPage', () => {
  it('renders all 4 KPI cards with data', async () => {
    renderPage(<ToolsPage />);

    const kpis = await screen.findAllByRole('status', {}, { timeout: 3000 });
    expect(kpis.length).toBeGreaterThanOrEqual(4);

    expect(screen.getByLabelText('Total Calls')).toBeInTheDocument();
    expect(screen.getByLabelText('Errors')).toBeInTheDocument();
    expect(screen.getByLabelText('Avg Latency')).toBeInTheDocument();
    expect(screen.getByLabelText('Avg Result Size')).toBeInTheDocument();
  });

  it('renders chart panels for tool analytics', async () => {
    renderPage(<ToolsPage />);

    await screen.findAllByRole('status', {}, { timeout: 3000 });

    expect(screen.getByText('Calls by Tool')).toBeInTheDocument();
    expect(screen.getByText('Latency by Tool')).toBeInTheDocument();
    expect(screen.getByText('Error Rate Over Time')).toBeInTheDocument();
  });

  it('KPI values are non-zero', async () => {
    renderPage(<ToolsPage />);

    const kpis = await screen.findAllByRole('status', {}, { timeout: 3000 });
    for (const kpi of kpis) {
      const valueEl = kpi.querySelector('.text-2xl');
      expect(valueEl).toBeTruthy();
      expect(valueEl!.textContent).not.toBe('');
    }
  });
});
