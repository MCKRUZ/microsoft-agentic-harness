import { describe, it, expect } from 'vitest';
import { screen } from '@testing-library/react';
import { SplitPanel } from '@/components/layout/SplitPanel';
import { renderWithProviders } from '@/test/utils';

describe('SplitPanel', () => {
  it('renders left and right children', () => {
    renderWithProviders(
      <SplitPanel left={<div>Left</div>} right={<div>Right</div>} />
    );
    expect(screen.getByText('Left')).toBeInTheDocument();
    expect(screen.getByText('Right')).toBeInTheDocument();
  });

  it('left panel is accessible (has landmark role or aria-label)', () => {
    renderWithProviders(
      <SplitPanel left={<div />} right={<div />} />
    );
    expect(screen.getByRole('main')).toBeInTheDocument();
  });
});
