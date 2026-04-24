import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { PanelCard } from './PanelCard';

describe('PanelCard', () => {
  it('renders title and children', () => {
    render(
      <PanelCard title="Test Panel">
        <div>Chart content</div>
      </PanelCard>,
    );
    expect(screen.getByText('Test Panel')).toBeInTheDocument();
    expect(screen.getByText('Chart content')).toBeInTheDocument();
  });

  it('renders description when provided', () => {
    render(
      <PanelCard title="Test" description="A description">
        <div />
      </PanelCard>,
    );
    expect(screen.getByText('A description')).toBeInTheDocument();
  });

  it('does not render description when not provided', () => {
    render(
      <PanelCard title="Test">
        <div />
      </PanelCard>,
    );
    const paragraphs = document.querySelectorAll('p');
    expect(paragraphs).toHaveLength(0);
  });

  it('applies custom className', () => {
    const { container } = render(
      <PanelCard title="Test" className="my-class">
        <div />
      </PanelCard>,
    );
    expect(container.firstChild).toHaveClass('my-class');
  });
});
