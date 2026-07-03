import { render, screen } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import { AgentTable } from '../AgentTable';

describe('AgentTable', () => {
  it('renders headers and cells from a valid spec', () => {
    render(<AgentTable args={{ title: 'Scores', columns: ['Name', 'Score'], rows: [['Ada', '97']] }} />);
    expect(screen.getByTestId('agent-table')).toBeInTheDocument();
    expect(screen.getByText('Scores')).toBeInTheDocument();
    expect(screen.getByRole('columnheader', { name: 'Name' })).toBeInTheDocument();
    expect(screen.getByRole('columnheader', { name: 'Score' })).toBeInTheDocument();
    expect(screen.getByRole('cell', { name: 'Ada' })).toBeInTheDocument();
    expect(screen.getByRole('cell', { name: '97' })).toBeInTheDocument();
  });

  it('renders a "No rows" affordance for a headers-only table', () => {
    render(<AgentTable args={{ columns: ['A', 'B'] }} />);
    expect(screen.getByText('No rows')).toBeInTheDocument();
  });

  it('shows a safe fallback (not a broken table) when there are no columns', () => {
    render(<AgentTable args={{ rows: [['x']] }} />);
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
    expect(screen.getByTestId('agent-table-fallback')).toBeInTheDocument();
  });

  it('does not render agent cell text as markup', () => {
    render(<AgentTable args={{ columns: ['A'], rows: [['<b>bold</b>']] }} />);
    // React escapes the string, so it appears as literal text, not an element.
    expect(screen.getByText('<b>bold</b>')).toBeInTheDocument();
  });
});
