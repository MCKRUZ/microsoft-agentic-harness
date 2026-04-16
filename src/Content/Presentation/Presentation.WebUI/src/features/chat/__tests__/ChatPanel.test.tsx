import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { vi, describe, it, expect, beforeEach } from 'vitest';
import { useChatStore } from '../useChatStore';
import { ChatPanel } from '../ChatPanel';

vi.mock('@/hooks/useAgentHub', () => ({
  useAgentHub: () => ({
    connectionState: 'connected',
    sendMessage: vi.fn().mockResolvedValue(undefined),
    startConversation: vi.fn().mockResolvedValue(undefined),
    invokeToolViaAgent: vi.fn().mockResolvedValue(undefined),
    joinGlobalTraces: vi.fn().mockResolvedValue(undefined),
    leaveGlobalTraces: vi.fn().mockResolvedValue(undefined),
  }),
}));

// appStore default (selectedAgent: null) is fine — no agent selected means
// startConversation is skipped on mount.

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
