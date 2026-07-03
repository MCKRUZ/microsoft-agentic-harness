import { render, screen } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import { renderWidget } from '../registry';

describe('widget registry', () => {
  it('renders the AgentImage component for a render_image widget', () => {
    render(<>{renderWidget({ type: 'render_image', args: { url: 'https://example.com/cat.png', caption: 'Fluffy' } })}</>);
    const img = screen.getByRole('img');
    expect(img).toHaveAttribute('src', 'https://example.com/cat.png');
    expect(screen.getByText('Fluffy')).toBeInTheDocument();
  });

  it('renders a safe fallback (not a broken image) for a non-https url', () => {
    render(<>{renderWidget({ type: 'render_image', args: { url: 'http://example.com/cat.png' } })}</>);
    expect(screen.queryByRole('img')).not.toBeInTheDocument();
    expect(screen.getByTestId('agent-image-fallback')).toBeInTheDocument();
  });

  it('renders nothing for an unknown widget type rather than throwing', () => {
    const { container } = render(<>{renderWidget({ type: 'render_hologram', args: {} })}</>);
    expect(container).toBeEmptyDOMElement();
  });
});
