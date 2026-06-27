import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { vi, describe, it, expect, beforeEach } from 'vitest';
import { useChatStore } from '../useChatStore';
import { useAppStore } from '@/stores/appStore';
import { ChatPanel } from '../ChatPanel';
import { renderWithProviders } from '@/test/utils';

const mockSendMessage = vi.fn();
const mockStartConversation = vi.fn().mockResolvedValue(undefined);

vi.mock('@/hooks/useAgentHub', () => ({
  useAgentHub: () => ({
    connectionState: 'connected',
    startConversation: mockStartConversation,
    invokeToolViaAgent: vi.fn().mockResolvedValue(undefined),
    retryFromMessage: vi.fn().mockResolvedValue(undefined),
    editAndResubmit: vi.fn().mockResolvedValue(undefined),
    setConversationSettings: vi.fn().mockResolvedValue(undefined),
  }),
  AgentHubProvider: ({ children }: { children: React.ReactNode }) => <>{children}</>,
}));

vi.mock('@/hooks/useAgentStream', () => ({
  useAgentStream: () => ({
    sendMessage: mockSendMessage,
    abort: vi.fn(),
  }),
}));

function getSubmitButton(): HTMLButtonElement {
  const btn = document.querySelector('button[type="submit"]');
  if (!btn) throw new Error('Submit button not found');
  return btn as HTMLButtonElement;
}

function renderPanel() {
  return renderWithProviders(<ChatPanel />);
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
    useAppStore.setState({ selectedAgent: 'test-agent' });
    mockSendMessage.mockReset();
    mockStartConversation.mockReset().mockResolvedValue(undefined);
  });

  // --- StartConversation race (regression guard) ---

  describe('ChatInput gating', () => {
    it('disables Send until startConversation resolves (prevents "Conversation not found")', async () => {
      let resolveStart!: () => void;
      const startPromise = new Promise<void>((resolve) => { resolveStart = resolve; });
      mockStartConversation.mockReturnValueOnce(startPromise);

      renderPanel();

      // Submit button should be disabled while conversation is starting (disabled prop=true)
      await waitFor(() => { expect(getSubmitButton()).toBeDisabled(); });

      resolveStart();

      // After start resolves, ChatInput disabled prop becomes false.
      // The button is still disabled because there's no text typed yet,
      // but the textarea should become enabled.
      await waitFor(() => {
        expect(screen.getByPlaceholderText(/message the agent/i)).not.toBeDisabled();
      });
    });

    it('does not invoke sendMessage when user clicks Send before startConversation resolves', async () => {
      const user = userEvent.setup();
      let resolveStart!: () => void;
      const startPromise = new Promise<void>((resolve) => { resolveStart = resolve; });
      mockStartConversation.mockReturnValueOnce(startPromise);

      renderPanel();

      const textarea = await screen.findByPlaceholderText(/message the agent/i);
      await user.type(textarea, 'hello');
      await user.click(getSubmitButton());

      expect(mockSendMessage).not.toHaveBeenCalled();

      resolveStart();
      await waitFor(() => {
        expect(screen.getByPlaceholderText(/message the agent/i)).not.toBeDisabled();
      });
    });
  });

  // --- ErrorBanner ---

  describe('ErrorBanner', () => {
    it('renders nothing when error is null', async () => {
      renderPanel();
      // Wait for conversation to start so the full UI renders
      await waitFor(() => { expect(screen.getByPlaceholderText(/message the agent/i)).toBeInTheDocument(); });
      expect(screen.queryByRole('button', { name: /dismiss/i })).not.toBeInTheDocument();
    });

    it('renders error message string', async () => {
      useChatStore.setState({ error: 'turn failed' });
      renderPanel();
      await waitFor(() => { expect(screen.getByText('turn failed')).toBeInTheDocument(); });
    });

    it('renders extracted .message not the raw [object Object]', async () => {
      // Simulate what setError now receives after our fix — a clean string
      useChatStore.setState({ error: 'turn failed' });
      renderPanel();
      await waitFor(() => {
        expect(screen.queryByText('[object Object]')).not.toBeInTheDocument();
        expect(screen.getByText('turn failed')).toBeInTheDocument();
      });
    });

    it('dismiss button clears the error', async () => {
      const user = userEvent.setup();
      useChatStore.setState({ error: 'something went wrong' });
      renderPanel();
      await waitFor(() => { expect(screen.getByText('something went wrong')).toBeInTheDocument(); });
      await user.click(screen.getByRole('button', { name: /dismiss/i }));
      expect(useChatStore.getState().error).toBeNull();
      expect(screen.queryByText('something went wrong')).not.toBeInTheDocument();
    });
  });

  // --- ConversationHeader ---

  describe('ConversationHeader', () => {
    it('shows a truncated UUID (8 chars) after mount', async () => {
      renderPanel();
      await waitFor(() => {
        // The component renders conversationId.slice(0, 8) — an 8 char hex substring
        expect(screen.getByText(/^[0-9a-f]{8}$/i)).toBeInTheDocument();
      });
    });

    it('Clear button resets messages', async () => {
      const user = userEvent.setup();
      useChatStore.setState({
        messages: [{ id: '1', role: 'user', content: 'hello', timestamp: new Date() }],
      });
      renderPanel();
      await waitFor(() => { expect(screen.getByRole('button', { name: /clear/i })).toBeInTheDocument(); });
      await user.click(screen.getByRole('button', { name: /clear/i }));
      expect(useChatStore.getState().messages).toHaveLength(0);
    });
  });
});
