import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/react';
import { PanelGrid } from './PanelGrid';

describe('PanelGrid', () => {
  it('renders children', () => {
    const { getByText } = render(
      <PanelGrid>
        <div>Child 1</div>
        <div>Child 2</div>
      </PanelGrid>,
    );
    expect(getByText('Child 1')).toBeInTheDocument();
    expect(getByText('Child 2')).toBeInTheDocument();
  });

  it('defaults to 3 columns', () => {
    const { container } = render(
      <PanelGrid>
        <div />
      </PanelGrid>,
    );
    expect(container.firstChild).toHaveClass('lg:grid-cols-3');
  });

  it('applies 2-column layout', () => {
    const { container } = render(
      <PanelGrid columns={2}>
        <div />
      </PanelGrid>,
    );
    expect(container.firstChild).toHaveClass('md:grid-cols-2');
    expect(container.firstChild).not.toHaveClass('lg:grid-cols-3');
  });

  it('applies 4-column layout', () => {
    const { container } = render(
      <PanelGrid columns={4}>
        <div />
      </PanelGrid>,
    );
    expect(container.firstChild).toHaveClass('lg:grid-cols-4');
  });

  it('applies custom className', () => {
    const { container } = render(
      <PanelGrid className="extra">
        <div />
      </PanelGrid>,
    );
    expect(container.firstChild).toHaveClass('extra');
  });
});
