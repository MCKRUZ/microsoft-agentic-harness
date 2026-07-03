import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { vi, describe, it, expect, beforeEach } from 'vitest';
import { AgentForm } from '../AgentForm';

const send = vi.fn<(text: string) => boolean>();
vi.mock('@/hooks/useSendUserMessage', () => ({ useSendUserMessage: () => send }));

describe('AgentForm', () => {
  beforeEach(() => { send.mockReset().mockReturnValue(true); });

  it('renders a fallback for an invalid spec instead of a broken form', () => {
    render(<AgentForm args={{ fields: [] }} />);
    expect(screen.getByTestId('agent-form-fallback')).toBeInTheDocument();
    expect(screen.queryByTestId('agent-form')).not.toBeInTheDocument();
  });

  it('renders a control per field and the title', () => {
    render(<AgentForm args={{ title: 'Preferences', fields: [
      { name: 'email', label: 'Email', type: 'text' },
      { name: 'bio', label: 'Bio', type: 'textarea' },
      { name: 'color', label: 'Color', type: 'select', options: ['red', 'blue'] },
      { name: 'news', label: 'Newsletter', type: 'checkbox' },
    ] }} />);
    expect(screen.getByText('Preferences')).toBeInTheDocument();
    expect(screen.getByLabelText('Email')).toBeInTheDocument();
    expect(screen.getByLabelText('Bio')).toBeInTheDocument();
    expect(screen.getByLabelText('Color')).toBeInTheDocument();
    expect(screen.getByLabelText('Newsletter')).toBeInTheDocument();
  });

  it('blocks submit and shows an error when a required field is blank', async () => {
    const user = userEvent.setup();
    render(<AgentForm args={{ fields: [{ name: 'email', label: 'Email', type: 'text', required: true }] }} />);
    await user.click(screen.getByRole('button', { name: /submit/i }));
    expect(send).not.toHaveBeenCalled();
    expect(screen.getByTestId('agent-form-error')).toHaveTextContent(/Email/);
  });

  it('on valid submit sends a formatted message and locks the form', async () => {
    const user = userEvent.setup();
    render(<AgentForm args={{ fields: [
      { name: 'email', label: 'Email', type: 'text', required: true },
      { name: 'news', label: 'Newsletter', type: 'checkbox' },
    ], submitLabel: 'Send it' }} />);

    // Required fields render their label with a trailing "*", so match by regex.
    await user.type(screen.getByLabelText(/Email/), 'a@b.com');
    await user.click(screen.getByLabelText('Newsletter'));
    await user.click(screen.getByRole('button', { name: 'Send it' }));

    expect(send).toHaveBeenCalledTimes(1);
    const message = send.mock.calls[0][0];
    expect(message).toContain('- Email: a@b.com');
    expect(message).toContain('- Newsletter: Yes');

    // Locked after submit — button relabeled and disabled, inputs disabled.
    const button = screen.getByRole('button', { name: /submitted/i });
    expect(button).toBeDisabled();
    expect(screen.getByLabelText(/Email/)).toBeDisabled();
  });

  it('does not lock the form when there is no active conversation to send to', async () => {
    send.mockReturnValue(false);
    const user = userEvent.setup();
    render(<AgentForm args={{ fields: [{ name: 'q', label: 'Q', type: 'text' }] }} />);
    await user.click(screen.getByRole('button', { name: /submit/i }));
    expect(screen.getByTestId('agent-form-error')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /submit/i })).not.toBeDisabled();
  });
});
