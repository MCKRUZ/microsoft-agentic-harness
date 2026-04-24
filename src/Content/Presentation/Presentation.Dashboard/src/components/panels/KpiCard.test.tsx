import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { KpiCard } from './KpiCard';

describe('KpiCard', () => {
  it('renders title and value', () => {
    render(<KpiCard title="Active Sessions" value="42" />);
    expect(screen.getByText('Active Sessions')).toBeInTheDocument();
    expect(screen.getByText('42')).toBeInTheDocument();
  });

  it('renders unit when provided', () => {
    render(<KpiCard title="Cost" value="$1.23" unit="USD" />);
    expect(screen.getByText('USD')).toBeInTheDocument();
  });

  it('does not render unit when not provided', () => {
    render(<KpiCard title="Count" value="10" />);
    expect(screen.queryByText('USD')).not.toBeInTheDocument();
  });

  it('renders sparkline when data has more than 1 point', () => {
    const data = [
      { timestamp: 1000, value: '1' },
      { timestamp: 2000, value: '2' },
      { timestamp: 3000, value: '3' },
    ];
    const { container } = render(<KpiCard title="Test" value="3" sparklineData={data} />);
    expect(container.querySelector('.recharts-responsive-container')).toBeInTheDocument();
  });

  it('does not render sparkline when data has 1 or fewer points', () => {
    const data = [{ timestamp: 1000, value: '1' }];
    const { container } = render(<KpiCard title="Test" value="1" sparklineData={data} />);
    expect(container.querySelector('.recharts-responsive-container')).not.toBeInTheDocument();
  });

  it('applies custom className', () => {
    const { container } = render(<KpiCard title="Test" value="1" className="custom-class" />);
    expect(container.firstChild).toHaveClass('custom-class');
  });
});
