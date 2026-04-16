import { render, screen, fireEvent } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { vi, describe, it, expect, beforeEach } from 'vitest';
import { useChatStore } from '../useChatStore';
import { ChatInput } from '../ChatInput';

const mockSendMessage = vi.fn().mockResolvedValue(undefined);

function renderInput() {
  return render(<ChatInput conversationId="test-conv" sendMessage={mockSendMessage} />);
}

describe('ChatInput', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    useChatStore.setState({ conversationId: null, messages: [], isStreaming: false, streamingContent: '' });
  });

  it('submit calls sendMessage with input value', async () => {
    const user = userEvent.setup();
    renderInput();
    await user.type(screen.getByPlaceholderText(/type a message/i), 'hello world');
    await user.click(screen.getByRole('button', { name: /send/i }));
    expect(mockSendMessage).toHaveBeenCalledWith('test-conv', 'hello world');
  });

  it('is disabled while isStreaming is true', () => {
    useChatStore.setState({ isStreaming: true });
    renderInput();
    expect(screen.getByPlaceholderText(/type a message/i)).toBeDisabled();
    expect(screen.getByRole('button', { name: /send/i })).toBeDisabled();
  });

  it('clears after submit', async () => {
    const user = userEvent.setup();
    renderInput();
    await user.type(screen.getByPlaceholderText(/type a message/i), 'hello world');
    await user.click(screen.getByRole('button', { name: /send/i }));
    expect(screen.getByPlaceholderText(/type a message/i)).toHaveValue('');
  });

  it('rejects empty string (does not call sendMessage)', async () => {
    const user = userEvent.setup();
    renderInput();
    await user.click(screen.getByRole('button', { name: /send/i }));
    expect(mockSendMessage).not.toHaveBeenCalled();
  });

  it('rejects messages over 4000 characters', async () => {
    renderInput();
    fireEvent.change(screen.getByPlaceholderText(/type a message/i), {
      target: { value: 'a'.repeat(4001) },
    });
    fireEvent.click(screen.getByRole('button', { name: /send/i }));
    expect(await screen.findByText(/message too long/i)).toBeInTheDocument();
    expect(mockSendMessage).not.toHaveBeenCalled();
  });
});
