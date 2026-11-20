import { cleanup, render, screen } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import { getWidget, registeredWidgetTypes, renderWidget } from '../registry';
import catalog from '../widgetCatalog.json';

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

  it('renders a visible placeholder, not nothing, for a saved widget type the registry no longer has', () => {
    render(<>{renderWidget({ type: 'render_hologram', args: {} })}</>);
    expect(screen.getByTestId('agent-widget-unsupported')).toBeInTheDocument();
  });

  it('renders a visible placeholder for a saved widget whose version this app no longer supports', () => {
    const current = catalog.widgets.render_table;
    render(<>{renderWidget({ type: 'render_table', version: current + 1, args: { columns: ['Name'], rows: [['Ada']] } })}</>);
    expect(screen.queryByTestId('agent-table')).not.toBeInTheDocument();
    expect(screen.getByTestId('agent-widget-unsupported')).toBeInTheDocument();
  });

  it('reads a saved widget with no version as version 1, the shape every pre-versioning widget was saved in', () => {
    const args = { columns: ['Name'], rows: [['Ada']] };
    const unversioned = render(<>{renderWidget({ type: 'render_table', args })}</>).container.innerHTML;
    cleanup();
    const versionOne = render(<>{renderWidget({ type: 'render_table', version: 1, args })}</>).container.innerHTML;
    expect(unversioned).toBe(versionOne);
  });

  it('renders a widget saved at its current version', () => {
    render(<>{renderWidget({ type: 'render_table', version: catalog.widgets.render_table, args: { columns: ['Name'], rows: [['Ada']] } })}</>);
    expect(screen.getByTestId('agent-table')).toBeInTheDocument();
  });

  it('registers exactly the widgets the shared catalogue declares, at the declared versions', () => {
    expect([...registeredWidgetTypes()].sort()).toEqual(Object.keys(catalog.widgets).sort());
    for (const [type, version] of Object.entries(catalog.widgets)) {
      expect(getWidget(type)?.version).toBe(version);
    }
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
