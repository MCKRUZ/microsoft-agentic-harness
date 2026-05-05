import { renderHook, act, waitFor } from '@testing-library/react';
import { vi, describe, it, expect, beforeEach } from 'vitest';
import { createElement, type ReactNode } from 'react';
import { AgentHubProvider, useAgentHub } from '../useAgentHub';
import { useChatStore } from '@/stores/chatStore';

const mocks = vi.hoisted(() => ({
  connectionStart: vi.fn(),
  connectionStop: vi.fn(),
  connectionOn: vi.fn(),
  connectionOff: vi.fn(),
  connectionInvoke: vi.fn(),
  onreconnecting: vi.fn(),
  onreconnected: vi.fn(),
  onclose: vi.fn(),
  buildHubConnection: vi.fn(),
  acquireTokenSilent: vi.fn(),
}));

const mockConnection = {
  start: mocks.connectionStart,
  stop: mocks.connectionStop,
  on: mocks.connectionOn,
  off: mocks.connectionOff,
  invoke: mocks.connectionInvoke,
  onreconnecting: mocks.onreconnecting,
  onreconnected: mocks.onreconnected,
  onclose: mocks.onclose,
};

vi.mock('@/lib/signalrClient', () => ({
  buildHubConnection: mocks.buildHubConnection,
}));

vi.mock('@azure/msal-react', () => ({
  useMsal: () => ({
    instance: {
      acquireTokenSilent: mocks.acquireTokenSilent,
      getAllAccounts: () => [{
        username: 'test@example.com',
        homeAccountId: '1',
        environment: '',
        tenantId: '',
        localAccountId: '',
        name: 'Test User',
      }],
    },
    accounts: [{
      username: 'test@example.com',
      homeAccountId: '1',
      environment: '',
      tenantId: '',
      localAccountId: '',
    }],
  }),
}));

vi.mock('@/lib/authConfig', () => ({
  loginRequest: { scopes: ['api://test/access_as_user'] },
}));

vi.mock('@/lib/devAuth', () => ({
  IS_AUTH_DISABLED: false,
}));

// Extract the callback registered for a named SignalR event.
// Must be called after renderHook so the useEffect has run.
function getHandler(eventName: string): (...args: unknown[]) => void {
  const call = mocks.connectionOn.mock.calls.find(
    ([name]: unknown[]) => name === eventName,
  );
  if (!call?.[1]) throw new Error(`No handler registered for '${eventName}'`);
  return call[1] as (...args: unknown[]) => void;
}

const wrapper = ({ children }: { children: ReactNode }) =>
  createElement(AgentHubProvider, null, children);

async function mountConnected() {
  const hook = renderHook(() => useAgentHub(), { wrapper });
  await waitFor(() => expect(hook.result.current.connectionState).toBe('connected'));
  return hook;
}

describe('useAgentHub', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mocks.buildHubConnection.mockReturnValue(mockConnection);
    mocks.connectionStart.mockResolvedValue(undefined);
    mocks.connectionStop.mockResolvedValue(undefined);
    mocks.acquireTokenSilent.mockResolvedValue({ accessToken: 'tok' });
    // Suppress the fetch('/api/config/auth') call made after connection starts
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(JSON.stringify({ authDisabled: false }), { status: 200 }));
    useChatStore.setState({
      conversationId: null,
      messages: [],
      isStreaming: false,
      streamingContent: '',
      error: null,
    });
  });

  // --- connection lifecycle ---

  it('starts in disconnected state', () => {
    mocks.connectionStart.mockReturnValue(new Promise<void>(() => {}));
    const { result } = renderHook(() => useAgentHub(), { wrapper });
    expect(result.current.connectionState).not.toBe('connected');
  });

  it('transitions to connected state after start()', async () => {
    const { result } = renderHook(() => useAgentHub(), { wrapper });
    await waitFor(() => {
      expect(result.current.connectionState).toBe('connected');
    });
  });

  it('cleanup calls connection.stop() on unmount', async () => {
    const { unmount } = renderHook(() => useAgentHub(), { wrapper });
    await waitFor(() => expect(mocks.connectionStop).not.toHaveBeenCalled());
    unmount();
    // Cleanup waits for start() to settle before calling stop(), so the call is async.
    await waitFor(() => { expect(mocks.connectionStop).toHaveBeenCalled(); });
  });

  it('sets chatStore error when connection.start() rejects', async () => {
    mocks.connectionStart.mockRejectedValue(new Error('ECONNREFUSED'));
    renderHook(() => useAgentHub(), { wrapper });
    await waitFor(() => {
      expect(useChatStore.getState().error).toBe('ECONNREFUSED');
    });
  });

  // --- Error event (system boundary — server can send any shape) ---

  describe('Error event', () => {
    it('stores a plain string error', async () => {
      await mountConnected();
      act(() => { getHandler('Error')('something went wrong'); });
      expect(useChatStore.getState().error).toBe('something went wrong');
    });

    it('extracts .message from {conversationId, message, code} object', async () => {
      await mountConnected();
      act(() => { getHandler('Error')({ conversationId: 'abc', message: 'turn failed', code: 500 }); });
      expect(useChatStore.getState().error).toBe('turn failed');
    });

    it('JSON.stringifies unknown object shapes', async () => {
      await mountConnected();
      act(() => { getHandler('Error')({ unexpected: true }); });
      expect(useChatStore.getState().error).toBe('{"unexpected":true}');
    });

    it('stores empty string for null payload', async () => {
      await mountConnected();
      act(() => { getHandler('Error')(null); });
      expect(useChatStore.getState().error).toBe('null');
    });
  });

  // --- TokenReceived event ---

  describe('TokenReceived event', () => {
    it('appends tokens and sets isStreaming', async () => {
      await mountConnected();
      act(() => { getHandler('TokenReceived')({ conversationId: 'c1', token: 'hello ', isComplete: false }); });
      act(() => { getHandler('TokenReceived')({ conversationId: 'c1', token: 'world', isComplete: false }); });
      const state = useChatStore.getState();
      expect(state.streamingContent).toBe('hello world');
      expect(state.isStreaming).toBe(true);
    });

    it('ignores the isComplete marker chunk', async () => {
      await mountConnected();
      act(() => { getHandler('TokenReceived')({ conversationId: 'c1', token: 'hi', isComplete: false }); });
      act(() => { getHandler('TokenReceived')({ conversationId: 'c1', token: 'hi', isComplete: true }); });
      expect(useChatStore.getState().streamingContent).toBe('hi');
    });
  });

  // --- TurnComplete event ---

  describe('TurnComplete event', () => {
    it('finalizes stream and adds an assistant message', async () => {
      await mountConnected();
      act(() => { getHandler('TokenReceived')({ conversationId: 'c1', token: 'partial', isComplete: false }); });
      act(() => { getHandler('TurnComplete')({ conversationId: 'c1', turnNumber: 2, fullResponse: 'full response' }); });
      const state = useChatStore.getState();
      expect(state.isStreaming).toBe(false);
      expect(state.streamingContent).toBe('');
      expect(state.messages).toHaveLength(1);
      expect(state.messages[0]?.role).toBe('assistant');
      expect(state.messages[0]?.content).toBe('full response');
    });
  });
});
