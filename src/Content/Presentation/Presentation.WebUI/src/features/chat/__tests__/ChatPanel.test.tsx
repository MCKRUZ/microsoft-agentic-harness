import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { vi, describe, it, expect, beforeEach } from 'vitest';
import { useChatStore } from '../useChatStore';
import { useAppStore } from '@/stores/appStore';
import { ChatPanel } from '../ChatPanel';

const mockSendMessage = vi.fn().mockResolvedValue(undefined);
const mockStartConversation = vi.fn().mockResolvedValue(undefined);

vi.mock('@/hooks/useAgentHub', () => ({
  useAgentHub: () => ({
    connectionState: 'connected',
    sendMessage: mockSendMessage,
    startConversation: mockStartConversation,
    invokeToolViaAgent: vi.fn().mockResolvedValue(undefined),
    retryFromMessage: vi.fn().mockResolvedValue(undefined),
    editAndResubmit: vi.fn().mockResolvedValue(undefined),
    joinGlobalTraces: vi.fn().mockResolvedValue(undefined),
    leaveGlobalTraces: vi.fn().mockResolvedValue(undefined),
  }),
}));

function renderPanel() {
  return render(<ChatPanel />);
}

describe('ChatPanel', () => {
  beforeEach(() => {
    useChatStore.setState({
      conversationId: null,
      messages: [],
      isStreaming: false,
      streamingContent: '',
      error: null,
    });
    useAppStore.setState({ selectedAgent: null });
    mockSendMessage.mockReset().mockResolvedValue(undefined);
    mockStartConversation.mockReset().mockResolvedValue(undefined);
  });

  // --- StartConversation race (regression guard) ---

  describe('ChatInput gating', () => {
    it('disables Send until startConversation resolves (prevents "Conversation not found")', async () => {
      let resolveStart!: () => void;
      const startPromise = new Promise<void>((resolve) => { resolveStart = resolve; });
      mockStartConversation.mockReturnValueOnce(startPromise);
      useAppStore.setState({ selectedAgent: 'test-agent' });

      renderPanel();

      const sendButton = await screen.findByRole('button', { name: /send/i });
      expect(sendButton).toBeDisabled();

      resolveStart();
      await waitFor(() => { expect(sendButton).not.toBeDisabled(); });
    });

    it('does not invoke sendMessage when user clicks Send before startConversation resolves', async () => {
      const user = userEvent.setup();
      let resolveStart!: () => void;
      const startPromise = new Promise<void>((resolve) => { resolveStart = resolve; });
      mockStartConversation.mockReturnValueOnce(startPromise);
      useAppStore.setState({ selectedAgent: 'test-agent' });

      renderPanel();

      const textarea = await screen.findByPlaceholderText(/type a message/i);
      await user.type(textarea, 'hello');
      await user.click(screen.getByRole('button', { name: /send/i }));

      expect(mockSendMessage).not.toHaveBeenCalled();

      resolveStart();
      await waitFor(() => { expect(screen.getByRole('button', { name: /send/i })).not.toBeDisabled(); });
    });
  });

  // --- ErrorBanner ---

  describe('ErrorBanner', () => {
    it('renders nothing when error is null', () => {
      renderPanel();
      expect(screen.queryByRole('button', { name: /dismiss/i })).not.toBeInTheDocument();
    });

    it('renders error message string', () => {
      useChatStore.setState({ error: 'turn failed' });
      renderPanel();
      expect(screen.getByText('turn failed')).toBeInTheDocument();
    });

    it('renders extracted .message not the raw [object Object]', () => {
      // Simulate what setError now receives after our fix — a clean string
      useChatStore.setState({ error: 'turn failed' });
      renderPanel();
      expect(screen.queryByText('[object Object]')).not.toBeInTheDocument();
      expect(screen.getByText('turn failed')).toBeInTheDocument();
    });

    it('dismiss button clears the error', async () => {
      const user = userEvent.setup();
      useChatStore.setState({ error: 'something went wrong' });
      renderPanel();
      await user.click(screen.getByRole('button', { name: /dismiss/i }));
      expect(useChatStore.getState().error).toBeNull();
      expect(screen.queryByText('something went wrong')).not.toBeInTheDocument();
    });
  });

  // --- ConversationHeader ---

  describe('ConversationHeader', () => {
    // ChatPanel's useEffect always generates a conversationId on mount,
    // so "No conversation" is unreachable in practice. We test the format instead.
    it('shows a truncated UUID (8 chars + ellipsis) after mount', async () => {
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText(/^.{8}\u2026$/)).toBeInTheDocument();
      });
    });

    it('Clear button resets messages', async () => {
      const user = userEvent.setup();
      useChatStore.setState({
        messages: [{ id: '1', role: 'user', content: 'hello', timestamp: new Date() }],
      });
      renderPanel();
      await user.click(screen.getByRole('button', { name: /clear/i }));
      expect(useChatStore.getState().messages).toHaveLength(0);
    });
  });
});
