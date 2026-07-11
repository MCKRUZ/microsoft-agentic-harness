import { screen, waitFor, act } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { vi, describe, it, expect, beforeEach } from 'vitest';
import { useChatStore } from '../useChatStore';
import { useAppStore } from '@/stores/appStore';
import type { ChatMessage } from '../useChatStore';
import { ChatPanel } from '../ChatPanel';
import { renderWithProviders } from '@/test/utils';

const mockSendMessage = vi.fn();
// startConversation must NOT be called on mount/navigation any more — it is deferred to the first
// user message (in useSendUserMessage). ChatPanel only ever loads history read-only.
const mockStartConversation = vi.fn().mockResolvedValue([]);

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

// ChatPanel loads the active conversation's transcript from this read-only helper (never creating it
// server-side). Mock it so tests drive history/gating without HTTP.
const mockLoadHistory = vi.fn<(id: string) => Promise<ChatMessage[]>>().mockResolvedValue([]);
vi.mock('../loadConversationHistory', () => ({
  loadConversationHistory: (id: string) => mockLoadHistory(id),
}));

const ACTIVE_ID = 'abcd1234-0000-0000-0000-000000000000';

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
    // Simulates the URL-synced active conversation id (ChatView writes the route param into the store).
    useAppStore.setState({ selectedAgent: 'test-agent', activeConversationId: ACTIVE_ID });
    mockSendMessage.mockReset();
    mockStartConversation.mockReset().mockResolvedValue([]);
    mockLoadHistory.mockReset().mockResolvedValue([]);
  });

  // --- Phantom-conversation regression (GitHub #96) ---

  describe('no phantom conversation on mount', () => {
    it('does NOT call hub startConversation when the panel mounts', async () => {
      renderPanel();
      await waitFor(() => { expect(screen.getByPlaceholderText(/message the agent/i)).not.toBeDisabled(); });
      expect(mockStartConversation).not.toHaveBeenCalled();
    });

    it('adopts the active conversation id from the store and loads its history (no id mint)', async () => {
      mockLoadHistory.mockResolvedValueOnce([
        { id: 'u1', role: 'user', content: 'earlier question', timestamp: new Date() },
      ]);
      renderPanel();

      expect(await screen.findByText('earlier question')).toBeInTheDocument();
      expect(mockLoadHistory).toHaveBeenCalledWith(ACTIVE_ID);
      // The id is unchanged — the panel never mints a new one on mount.
      expect(useAppStore.getState().activeConversationId).toBe(ACTIVE_ID);
    });

    it('with a blank composer (no active id) it loads nothing and stays enabled', async () => {
      useAppStore.setState({ activeConversationId: null });
      renderPanel();
      await waitFor(() => { expect(screen.getByPlaceholderText(/message the agent/i)).not.toBeDisabled(); });
      expect(mockLoadHistory).not.toHaveBeenCalled();
      expect(mockStartConversation).not.toHaveBeenCalled();
    });

    it('reloads the target transcript when the active id changes via back/forward with the previous conversation still in the store', async () => {
      // Regression: switching conversations with the browser back/forward buttons changes only the URL
      // → the store's active id. No click handler runs, so the previous conversation's messages are
      // still in the store. The panel must reload the new id's history and replace the stale transcript
      // rather than leaving conversation A's messages on screen under conversation B's URL. Gating the
      // skip on transcript *ownership* (chatStore.conversationId) — not mere message presence — is what
      // makes this correct.
      const CONV_B = 'ffff5678-0000-0000-0000-000000000000';

      mockLoadHistory.mockResolvedValueOnce([
        { id: 'a1', role: 'user', content: 'A question', timestamp: new Date() },
      ]);
      renderPanel();
      expect(await screen.findByText('A question')).toBeInTheDocument();

      mockLoadHistory.mockResolvedValueOnce([
        { id: 'b1', role: 'user', content: 'B question', timestamp: new Date() },
      ]);
      act(() => {
        useAppStore.setState({ activeConversationId: CONV_B });
      });

      expect(await screen.findByText('B question')).toBeInTheDocument();
      expect(mockLoadHistory).toHaveBeenLastCalledWith(CONV_B);
      expect(screen.queryByText('A question')).not.toBeInTheDocument();
    });
  });

  // --- Composer gating on history load ---

  describe('composer gating', () => {
    it('disables the composer until history load resolves', async () => {
      let resolveLoad!: (m: ChatMessage[]) => void;
      mockLoadHistory.mockReturnValueOnce(new Promise<ChatMessage[]>((resolve) => { resolveLoad = resolve; }));

      renderPanel();

      await waitFor(() => { expect(screen.getByPlaceholderText(/message the agent/i)).toBeDisabled(); });

      resolveLoad([]);

      await waitFor(() => {
        expect(screen.getByPlaceholderText(/message the agent/i)).not.toBeDisabled();
      });
    });
  });

  // --- Widget persistence on reload ---

  describe('widget re-render on reload', () => {
    it('renders a persisted widget message returned by the history loader', async () => {
      mockLoadHistory.mockResolvedValueOnce([
        {
          id: 'w1',
          role: 'assistant',
          content: '',
          timestamp: new Date(),
          widget: { type: 'render_table', args: { columns: ['Name'], rows: [['Ada']] } },
        },
      ]);

      renderPanel();

      expect(await screen.findByTestId('agent-table')).toBeInTheDocument();
      expect(screen.getByRole('cell', { name: 'Ada' })).toBeInTheDocument();
    });
  });

  // --- ErrorBanner ---

  describe('ErrorBanner', () => {
    it('renders nothing when error is null', async () => {
      renderPanel();
      await waitFor(() => { expect(screen.getByPlaceholderText(/message the agent/i)).toBeInTheDocument(); });
      expect(screen.queryByRole('button', { name: /dismiss/i })).not.toBeInTheDocument();
    });

    it('renders error message string', async () => {
      useChatStore.setState({ error: 'turn failed' });
      renderPanel();
      await waitFor(() => { expect(screen.getByText('turn failed')).toBeInTheDocument(); });
    });

    it('renders extracted .message not the raw [object Object]', async () => {
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
    it('shows the truncated active conversation id (8 chars)', async () => {
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText(ACTIVE_ID.slice(0, 8))).toBeInTheDocument();
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
