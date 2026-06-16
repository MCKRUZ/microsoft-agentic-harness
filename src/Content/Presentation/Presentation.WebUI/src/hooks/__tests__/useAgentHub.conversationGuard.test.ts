import { renderHook, act, waitFor } from '@testing-library/react';
import { vi, describe, it, expect, beforeEach } from 'vitest';
import { createElement, type ReactNode } from 'react';
import { AgentHubProvider, useAgentHub } from '../useAgentHub';
import { useChatStore } from '@/stores/chatStore';

// Regression coverage for the solution-review finding: streaming handlers must
// drop payloads whose conversationId does not match the store's active
// conversation, so tokens from conversation A never render or finalize into
// conversation B's transcript after a mid-stream conversation switch.

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

describe('useAgentHub conversation guard', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mocks.buildHubConnection.mockReturnValue(mockConnection);
    mocks.connectionStart.mockResolvedValue(undefined);
    mocks.connectionStop.mockResolvedValue(undefined);
    mocks.acquireTokenSilent.mockResolvedValue({ accessToken: 'tok' });
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(
      new Response(JSON.stringify({ authDisabled: false }), { status: 200 }),
    );
    useChatStore.setState({
      conversationId: null,
      messages: [],
      isStreaming: false,
      streamingContent: '',
      error: null,
    });
  });

  it('TokenReceived_ForeignConversation_DropsToken', async () => {
    await mountConnected();
    useChatStore.getState().setConversationId('conv-B');

    act(() => {
      getHandler('TokenReceived')({ conversationId: 'conv-A', token: 'leak', isComplete: false });
    });

    const state = useChatStore.getState();
    expect(state.streamingContent).toBe('');
    expect(state.isStreaming).toBe(false);
  });

  it('TokenReceived_ActiveConversation_AppendsToken', async () => {
    await mountConnected();
    useChatStore.getState().setConversationId('conv-B');

    act(() => {
      getHandler('TokenReceived')({ conversationId: 'conv-B', token: 'hi', isComplete: false });
    });

    expect(useChatStore.getState().streamingContent).toBe('hi');
  });

  it('TurnComplete_ForeignConversation_DoesNotFinalizeIntoActiveTranscript', async () => {
    await mountConnected();
    useChatStore.getState().setConversationId('conv-B');

    act(() => {
      getHandler('TurnComplete')({
        conversationId: 'conv-A',
        turnNumber: 1,
        fullResponse: 'A full reply',
      });
    });

    expect(useChatStore.getState().messages).toHaveLength(0);
  });

  it('HistoryTruncated_ForeignConversation_DoesNotTruncateActiveTranscript', async () => {
    await mountConnected();
    useChatStore.setState({
      conversationId: 'conv-B',
      messages: [
        { id: 'm1', role: 'user', content: 'one', timestamp: new Date() },
        { id: 'm2', role: 'assistant', content: 'two', timestamp: new Date() },
      ],
    });

    act(() => {
      getHandler('HistoryTruncated')({ conversationId: 'conv-A', keepCount: 0 });
    });

    expect(useChatStore.getState().messages).toHaveLength(2);
  });
});
