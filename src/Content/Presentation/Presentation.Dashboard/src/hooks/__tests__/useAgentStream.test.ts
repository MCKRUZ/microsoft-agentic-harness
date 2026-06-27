import { renderHook, act, waitFor } from '@testing-library/react';
import { vi, describe, it, expect, beforeEach } from 'vitest';
import { useAgentStream } from '../useAgentStream';
import { useChatStore } from '@/stores/chatStore';

const mocks = vi.hoisted(() => ({
  createAgent: vi.fn(),
}));

vi.mock('@/lib/agUiClient', () => ({
  createAuthenticatedAgUiAgent: mocks.createAgent,
}));

vi.mock('@azure/msal-react', () => ({
  useMsal: () => ({ instance: { getAllAccounts: () => [], acquireTokenSilent: vi.fn() } }),
}));

vi.mock('@/auth/authConfig', () => ({ loginRequest: { scopes: [] } }));
vi.mock('@/auth/devAuth', () => ({ IS_AUTH_DISABLED: true }));

describe('useAgentStream', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    useChatStore.setState({
      conversationId: null,
      messages: [],
      isStreaming: false,
      streamingContent: '',
      error: null,
    });
  });

  it('clears the streaming flag and surfaces an error when agent construction fails', async () => {
    mocks.createAgent.mockRejectedValue(new Error('boom'));
    const { result } = renderHook(() => useAgentStream());

    act(() => {
      result.current.sendMessage('conv-1', 'msg-1', 'hello');
    });

    // Streaming is set optimistically before the async agent build.
    expect(useChatStore.getState().isStreaming).toBe(true);

    // The .catch must clear it once construction rejects (regression for the
    // stuck-streaming footgun fixed in this PR).
    await waitFor(() => expect(useChatStore.getState().isStreaming).toBe(false));
    expect(useChatStore.getState().error).toBe('boom');
  });

  it('does not surface an error from a superseded run', async () => {
    let rejectFirst!: (e: Error) => void;
    mocks.createAgent
      .mockImplementationOnce(() => new Promise((_, reject) => { rejectFirst = reject; }))
      .mockImplementationOnce(() => new Promise(() => { /* second run stays pending */ }));

    const { result } = renderHook(() => useAgentStream());

    act(() => { result.current.sendMessage('conv-1', 'm1', 'first'); });
    act(() => { result.current.sendMessage('conv-1', 'm2', 'second'); }); // bumps the run token

    act(() => { rejectFirst(new Error('stale')); });
    await act(async () => { await Promise.resolve(); });

    // The first run was superseded, so its rejection must be swallowed.
    expect(useChatStore.getState().error).not.toBe('stale');
  });
});
