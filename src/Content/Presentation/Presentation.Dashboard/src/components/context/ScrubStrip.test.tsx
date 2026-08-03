import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { ScrubStrip, type ScrubTurn } from './ScrubStrip';

const turns: ScrubTurn[] = [
  { id: 't-01', type: 'user', tokens: 520, label: 'U1' },
  { id: 't-02', type: 'assistant', tokens: 4_540, label: 'A2' },
  { id: 't-03', type: 'tool', tokens: 1_200, label: 'T3' },
  { id: 't-04', type: 'assistant', tokens: 800, label: 'A4' },
];

describe('ScrubStrip', () => {
  it('renders one dot per turn', () => {
    render(<ScrubStrip turns={turns} activeIndex={-1} onScrub={() => {}} />);
    expect(screen.getByTestId('scrub-strip-dot-0')).toBeInTheDocument();
    expect(screen.getByTestId('scrub-strip-dot-1')).toBeInTheDocument();
    expect(screen.getByTestId('scrub-strip-dot-2')).toBeInTheDocument();
    expect(screen.getByTestId('scrub-strip-dot-3')).toBeInTheDocument();
  });

  it('marks the dot at activeIndex with data-active', () => {
    render(<ScrubStrip turns={turns} activeIndex={2} onScrub={() => {}} />);
    expect(screen.getByTestId('scrub-strip-dot-2')).toHaveAttribute('data-active', 'true');
    expect(screen.getByTestId('scrub-strip-dot-0')).toHaveAttribute('data-active', 'false');
  });

  it('fires onScrub with the clicked index', () => {
    const onScrub = vi.fn();
    render(<ScrubStrip turns={turns} activeIndex={-1} onScrub={onScrub} />);
    fireEvent.click(screen.getByTestId('scrub-strip-dot-1'));
    expect(onScrub).toHaveBeenCalledWith(1);
  });

  it('tags each dot with its role', () => {
    render(<ScrubStrip turns={turns} activeIndex={-1} onScrub={() => {}} />);
    expect(screen.getByTestId('scrub-strip-dot-0')).toHaveAttribute('data-role', 'user');
    expect(screen.getByTestId('scrub-strip-dot-2')).toHaveAttribute('data-role', 'tool');
  });

  it('omits the sparkline by default', () => {
    render(<ScrubStrip turns={turns} activeIndex={-1} onScrub={() => {}} />);
    expect(screen.queryByTestId('scrub-strip-sparkline')).not.toBeInTheDocument();
  });

  it('renders sparkline path when showSparkline is true', () => {
    render(
      <ScrubStrip turns={turns} activeIndex={-1} onScrub={() => {}} showSparkline />,
    );
    const svg = screen.getByTestId('scrub-strip-sparkline');
    expect(svg).toBeInTheDocument();
    const path = svg.querySelector('path');
    expect(path).not.toBeNull();
    // Path should start with M (moveto) and contain at least one L (lineto)
    expect(path?.getAttribute('d')).toMatch(/^M[\d.]+,[\d.]+\s+L/);
  });

  it('does not render sparkline when only one turn exists', () => {
    // turns is the 4-element literal declared above, so index 0 is present by
    // construction — the non-null assertion states that rather than guessing at it.
    render(
      <ScrubStrip
        turns={[turns[0]!]}
        activeIndex={-1}
        onScrub={() => {}}
        showSparkline
      />,
    );
    expect(screen.queryByTestId('scrub-strip-sparkline')).not.toBeInTheDocument();
  });

  it('renders labels under dots when any turn has a label', () => {
    render(<ScrubStrip turns={turns} activeIndex={-1} onScrub={() => {}} />);
    expect(screen.getByText('U1')).toBeInTheDocument();
    expect(screen.getByText('T3')).toBeInTheDocument();
  });

  it('renders a finite sparkline path when every turn has zero tokens', () => {
    const zeros: ScrubTurn[] = turns.map((t) => ({ ...t, tokens: 0 }));
    render(
      <ScrubStrip turns={zeros} activeIndex={-1} onScrub={() => {}} showSparkline />,
    );
    const svg = screen.getByTestId('scrub-strip-sparkline');
    const d = svg.querySelector('path')?.getAttribute('d') ?? '';
    expect(d).not.toContain('NaN');
    expect(d).toMatch(/^M[\d.]+,[\d.]+/);
  });
});
