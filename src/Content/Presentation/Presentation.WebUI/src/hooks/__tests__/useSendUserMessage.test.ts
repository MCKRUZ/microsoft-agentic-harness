import { renderHook, waitFor } from '@testing-library/react';
import { vi, describe, it, expect, beforeEach } from 'vitest';
import { useSendUserMessage } from '../useSendUserMessage';
import { useChatStore } from '@/stores/chatStore';
import { useAppStore } from '@/stores/appStore';

// The send path is where a conversation is lazily created (first message) and where a blank composer
// mints its id. These mocks isolate that contract from SignalR / the AG-UI stream / MSAL / the router.
const agUiSend = vi.fn();
vi.mock('@/hooks/useAgentStream', () => ({
  useAgentStream: () => ({ sendMessage: agUiSend, abort: vi.fn() }),
}));

const startConversation = vi.fn().mockResolvedValue([]);
let connectionState = 'connected';
vi.mock('@/hooks/useAgentHub', () => ({
  useAgentHub: () => ({ startConversation, connectionState }),
}));

const navigate = vi.fn();
vi.mock('react-router-dom', () => ({
  useNavigate: () => navigate,
}));

const invalidateQueries = vi.fn();
vi.mock('@tanstack/react-query', () => ({
  useQueryClient: () => ({ invalidateQueries }),
}));

describe('useSendUserMessage', () => {
  beforeEach(() => {
    agUiSend.mockReset();
    startConversation.mockReset().mockResolvedValue([]);
    navigate.mockReset();
    invalidateQueries.mockReset();
    useChatStore.getState().clearMessages();
    useChatStore.getState().setError(null);
    useAppStore.setState({ activeConversationId: null, selectedAgent: null });
    connectionState = 'connected';
  });

  it('sends nothing and returns false when no agent is selected', () => {
    useAppStore.setState({ activeConversationId: 'conv-1', selectedAgent: null });
    const { result } = renderHook(() => useSendUserMessage());

    expect(result.current('hi')).toBe(false);
    expect(agUiSend).not.toHaveBeenCalled();
    expect(startConversation).not.toHaveBeenCalled();
    expect(useChatStore.getState().messages).toHaveLength(0);
  });

  it('registers the conversation exactly once on the FIRST message, then dispatches the run', async () => {
    useAppStore.setState({ activeConversationId: 'conv-1', selectedAgent: 'agent-a' });
    const { result } = renderHook(() => useSendUserMessage());

    expect(result.current('hello there')).toBe(true);

    // Optimistic user message is added synchronously and streaming starts.
    const messages = useChatStore.getState().messages;
    expect(messages).toHaveLength(1);
    expect(messages[0]).toMatchObject({ role: 'user', content: 'hello there' });
    expect(useChatStore.getState().isStreaming).toBe(true);

    // The server-side conversation is created before the run is dispatched.
    await waitFor(() => { expect(agUiSend).toHaveBeenCalledWith('conv-1', expect.any(String), 'hello there'); });
    expect(startConversation).toHaveBeenCalledTimes(1);
    expect(startConversation).toHaveBeenCalledWith('agent-a', 'conv-1');
    expect(invalidateQueries).toHaveBeenCalledTimes(1);
    // An existing id in the store is not a new conversation — no navigation/mint.
    expect(navigate).not.toHaveBeenCalled();
  });

  it('does NOT register the conversation again on a subsequent message', () => {
    useAppStore.setState({ activeConversationId: 'conv-1', selectedAgent: 'agent-a' });
    // A non-empty transcript marks this as an ongoing conversation, not a first message.
    useChatStore.getState().addMessage({ id: 'm0', role: 'assistant', content: 'earlier', timestamp: new Date() });
    const { result } = renderHook(() => useSendUserMessage());

    expect(result.current('follow up')).toBe(true);

    expect(startConversation).not.toHaveBeenCalled();
    // Ongoing conversations dispatch synchronously (no create round-trip to await).
    expect(agUiSend).toHaveBeenCalledWith('conv-1', expect.any(String), 'follow up');
  });

  it('refuses to send while the hub is not connected — no mint, no create, no dispatch', () => {
    connectionState = 'connecting';
    useAppStore.setState({ activeConversationId: null, selectedAgent: 'agent-a' });
    const { result } = renderHook(() => useSendUserMessage());

    expect(result.current('too early')).toBe(false);
    expect(startConversation).not.toHaveBeenCalled();
    expect(agUiSend).not.toHaveBeenCalled();
    expect(navigate).not.toHaveBeenCalled();
    // Nothing minted server-side or locally; the user gets told to wait.
    expect(useAppStore.getState().activeConversationId).toBeNull();
    expect(useChatStore.getState().messages).toHaveLength(0);
    expect(useChatStore.getState().error).toBeTruthy();
  });

  it('rolls back the optimistic message when the first-message create fails so a retry re-attempts it', async () => {
    useAppStore.setState({ activeConversationId: 'conv-1', selectedAgent: 'agent-a' });
    startConversation.mockRejectedValueOnce(new Error('hub down'));
    const { result } = renderHook(() => useSendUserMessage());

    expect(result.current('first try')).toBe(true);

    // The failed create rolls the transcript back to empty and surfaces the error — no dangling
    // optimistic message that would wedge every future send.
    await waitFor(() => { expect(useChatStore.getState().error).toBeTruthy(); });
    expect(useChatStore.getState().messages).toHaveLength(0);
    expect(agUiSend).not.toHaveBeenCalled();

    // Retry: the empty transcript means this still counts as the first message, so StartConversation is
    // attempted again (idempotent on the reused id) and this time the run dispatches.
    expect(result.current('second try')).toBe(true);
    await waitFor(() => { expect(agUiSend).toHaveBeenCalledWith('conv-1', expect.any(String), 'second try'); });
    expect(startConversation).toHaveBeenCalledTimes(2);
  });

  it('mints an id, reflects it in the store + URL, and registers it once when the composer is blank', async () => {
    useAppStore.setState({ activeConversationId: null, selectedAgent: 'agent-a' });
    const { result } = renderHook(() => useSendUserMessage());

    expect(result.current('first message')).toBe(true);

    const mintedId = useAppStore.getState().activeConversationId;
    expect(mintedId).toEqual(expect.any(String));
    expect(mintedId).not.toBeNull();
    expect(navigate).toHaveBeenCalledWith(`/chat/${mintedId}`, { replace: true });

    await waitFor(() => { expect(startConversation).toHaveBeenCalledWith('agent-a', mintedId); });
    expect(startConversation).toHaveBeenCalledTimes(1);
    await waitFor(() => { expect(agUiSend).toHaveBeenCalledWith(mintedId, expect.any(String), 'first message'); });
  });
});
