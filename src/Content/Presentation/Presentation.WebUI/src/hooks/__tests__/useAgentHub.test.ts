import { renderHook, act, waitFor } from '@testing-library/react';
import { vi, describe, it, expect, beforeEach } from 'vitest';
import { useAgentHub } from '../useAgentHub';
import { useChatStore } from '@/stores/chatStore';
import { useTelemetryStore } from '@/stores/telemetryStore';

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
    instance: { acquireTokenSilent: mocks.acquireTokenSilent },
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

// Extract the callback registered for a named SignalR event.
// Must be called after renderHook so the useEffect has run.
function getHandler(eventName: string): (...args: unknown[]) => void {
  const call = mocks.connectionOn.mock.calls.find(
    ([name]: unknown[]) => name === eventName,
  );
  if (!call?.[1]) throw new Error(`No handler registered for '${eventName}'`);
  return call[1] as (...args: unknown[]) => void;
}

async function mountConnected() {
  const hook = renderHook(() => useAgentHub());
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
    useChatStore.setState({
      conversationId: null,
      messages: [],
      isStreaming: false,
      streamingContent: '',
      error: null,
    });
    useTelemetryStore.setState({ conversationSpans: {}, globalSpans: [] });
  });

  // --- connection lifecycle ---

  it('starts in disconnected state', () => {
    mocks.connectionStart.mockReturnValue(new Promise<void>(() => {}));
    const { result } = renderHook(() => useAgentHub());
    expect(result.current.connectionState).not.toBe('connected');
  });

  it('transitions to connected state after start()', async () => {
    const { result } = renderHook(() => useAgentHub());
    await waitFor(() => {
      expect(result.current.connectionState).toBe('connected');
    });
  });

  it('cleanup calls connection.stop() on unmount', async () => {
    const { unmount } = renderHook(() => useAgentHub());
    await waitFor(() => expect(mocks.connectionStop).not.toHaveBeenCalled());
    unmount();
    expect(mocks.connectionStop).toHaveBeenCalled();
  });

  it('sets chatStore error when connection.start() rejects', async () => {
    mocks.connectionStart.mockRejectedValue(new Error('ECONNREFUSED'));
    renderHook(() => useAgentHub());
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
      act(() => { getHandler('TokenReceived')('hello '); });
      act(() => { getHandler('TokenReceived')('world'); });
      const state = useChatStore.getState();
      expect(state.streamingContent).toBe('hello world');
      expect(state.isStreaming).toBe(true);
    });
  });

  // --- TurnComplete event ---

  describe('TurnComplete event', () => {
    it('finalizes stream and adds an assistant message', async () => {
      await mountConnected();
      act(() => { getHandler('TokenReceived')('partial'); });
      act(() => { getHandler('TurnComplete')({ content: 'full response' }); });
      const state = useChatStore.getState();
      expect(state.isStreaming).toBe(false);
      expect(state.streamingContent).toBe('');
      expect(state.messages).toHaveLength(1);
      expect(state.messages[0]?.role).toBe('assistant');
      expect(state.messages[0]?.content).toBe('full response');
    });
  });

  // --- ConversationHistory event ---

  describe('ConversationHistory event', () => {
    it('replaces messages in the store', async () => {
      await mountConnected();
      const history = [
        { id: '1', role: 'user', content: 'hi', timestamp: new Date() },
        { id: '2', role: 'assistant', content: 'hello', timestamp: new Date() },
      ];
      act(() => { getHandler('ConversationHistory')(history); });
      const state = useChatStore.getState();
      expect(state.messages).toHaveLength(2);
      expect(state.messages[0]?.content).toBe('hi');
      expect(state.messages[1]?.content).toBe('hello');
    });
  });

  // --- SpanReceived event ---

  describe('SpanReceived event', () => {
    it('adds span to telemetry store', async () => {
      await mountConnected();
      const span = {
        name: 'TestOp',
        traceId: 'trace-1',
        spanId: 'span-1',
        parentSpanId: null,
        conversationId: 'conv-1',
        startTime: new Date().toISOString(),
        durationMs: 42,
        status: 'ok' as const,
        kind: 'internal',
        sourceName: 'test',
        tags: {},
      };
      act(() => { getHandler('SpanReceived')(span); });
      const state = useTelemetryStore.getState();
      expect(state.globalSpans).toHaveLength(1);
      expect(state.globalSpans[0]?.spanId).toBe('span-1');
    });
  });
});
