import { render, screen } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import { renderWidget } from '../registry';

// AgentForm (render_form) pulls in the send hook → MSAL; stub it so the registry test stays isolated.
vi.mock('@/hooks/useSendUserMessage', () => ({ useSendUserMessage: () => () => true }));

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

  it('renders the AgentForm component for a render_form widget', () => {
    render(<>{renderWidget({ type: 'render_form', args: { title: 'Sign up', fields: [{ name: 'email', label: 'Email', type: 'text' }] } })}</>);
    expect(screen.getByTestId('agent-form')).toBeInTheDocument();
    expect(screen.getByLabelText('Email')).toBeInTheDocument();
  });

  it('renders the AgentTable component for a render_table widget', () => {
    render(<>{renderWidget({ type: 'render_table', args: { columns: ['Name', 'Score'], rows: [['Ada', '97']] } })}</>);
    expect(screen.getByTestId('agent-table')).toBeInTheDocument();
    expect(screen.getByRole('cell', { name: 'Ada' })).toBeInTheDocument();
  });

  it('renders a safe fallback (not a broken table) for a render_table widget with no columns', () => {
    render(<>{renderWidget({ type: 'render_table', args: { rows: [['x']] } })}</>);
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
    expect(screen.getByTestId('agent-table-fallback')).toBeInTheDocument();
  });
});
