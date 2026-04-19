import { screen, fireEvent, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { vi, describe, it, expect, beforeEach } from 'vitest';
import { useChatStore } from '../useChatStore';
import { ChatInput } from '../ChatInput';
import { renderWithProviders } from '@/test/utils';

vi.mock('@/features/mcp/useMcpQuery', () => ({
  usePromptsQuery: () => ({
    data: [
      { name: 'summarize', description: 'Summarize text' },
      { name: 'translate', description: 'Translate text' },
    ],
    isLoading: false,
  }),
  useToolsQuery: () => ({
    data: [
      { name: 'search', description: 'Web search' },
      { name: 'calculator', description: 'Math' },
    ],
    isLoading: false,
  }),
}));

const mockSendMessage = vi.fn().mockResolvedValue(undefined);

function renderInput() {
  return renderWithProviders(<ChatInput conversationId="test-conv" sendMessage={mockSendMessage} />);
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
    expect(mockSendMessage).toHaveBeenCalledWith('test-conv', expect.any(String), 'hello world');
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

  it('rejects messages over 40000 characters', async () => {
    renderInput();
    fireEvent.change(screen.getByPlaceholderText(/type a message/i), {
      target: { value: 'a'.repeat(40_001) },
    });
    fireEvent.click(screen.getByRole('button', { name: /send/i }));
    expect(await screen.findByText(/message too long/i)).toBeInTheDocument();
    expect(mockSendMessage).not.toHaveBeenCalled();
  });

  it('shows prompt picker when user types @', async () => {
    const user = userEvent.setup();
    renderInput();
    await user.type(screen.getByPlaceholderText(/type a message/i), '@');
    expect(await screen.findByRole('listbox', { name: /prompts/i })).toBeInTheDocument();
    expect(screen.getByText('summarize')).toBeInTheDocument();
    expect(screen.getByText('translate')).toBeInTheDocument();
  });

  it('shows tool picker when user types /', async () => {
    const user = userEvent.setup();
    renderInput();
    await user.type(screen.getByPlaceholderText(/type a message/i), '/');
    expect(await screen.findByRole('listbox', { name: /tools/i })).toBeInTheDocument();
    expect(screen.getByText('search')).toBeInTheDocument();
    expect(screen.getByText('calculator')).toBeInTheDocument();
  });

  it('filters picker items as user types after @', async () => {
    const user = userEvent.setup();
    renderInput();
    await user.type(screen.getByPlaceholderText(/type a message/i), '@tra');
    await waitFor(() => {
      expect(screen.getByText('translate')).toBeInTheDocument();
      expect(screen.queryByText('summarize')).not.toBeInTheDocument();
    });
  });

  it('inserts selection on Enter and closes picker', async () => {
    const user = userEvent.setup();
    renderInput();
    const input = screen.getByPlaceholderText(/type a message/i);
    await user.type(input, '@sum');
    await user.keyboard('{Enter}');
    expect(input).toHaveValue('@summarize ');
    expect(screen.queryByRole('listbox', { name: /prompts/i })).not.toBeInTheDocument();
  });

  it('closes picker on Escape without inserting', async () => {
    const user = userEvent.setup();
    renderInput();
    const input = screen.getByPlaceholderText(/type a message/i);
    await user.type(input, '@sum');
    await user.keyboard('{Escape}');
    expect(input).toHaveValue('@sum');
    expect(screen.queryByRole('listbox', { name: /prompts/i })).not.toBeInTheDocument();
  });
});
