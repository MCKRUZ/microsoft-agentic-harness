import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { EmptyState } from './EmptyState';

describe('EmptyState', () => {
  it('renders title', () => {
    render(<EmptyState title="No data available" />);
    expect(screen.getByText('No data available')).toBeInTheDocument();
  });

  it('renders description when provided', () => {
    render(<EmptyState title="No data" description="Try adjusting the time range" />);
    expect(screen.getByText('Try adjusting the time range')).toBeInTheDocument();
  });

  it('does not render description when not provided', () => {
    render(<EmptyState title="No data" />);
    const paragraphs = document.querySelectorAll('p');
    expect(paragraphs).toHaveLength(0);
  });

  it('renders icon when provided', () => {
    render(<EmptyState title="No data" icon={<span data-testid="icon">!</span>} />);
    expect(screen.getByTestId('icon')).toBeInTheDocument();
  });

  it('applies custom className', () => {
    const { container } = render(<EmptyState title="Test" className="my-empty" />);
    expect(container.firstChild).toHaveClass('my-empty');
  });
});
