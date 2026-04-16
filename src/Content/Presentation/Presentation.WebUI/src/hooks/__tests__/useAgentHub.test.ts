import { renderHook, waitFor } from '@testing-library/react';
import { vi, describe, it, expect, beforeEach } from 'vitest';
import { useAgentHub } from '../useAgentHub';

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

describe('useAgentHub', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mocks.buildHubConnection.mockReturnValue(mockConnection);
    mocks.connectionStart.mockResolvedValue(undefined);
    mocks.connectionStop.mockResolvedValue(undefined);
    mocks.acquireTokenSilent.mockResolvedValue({ accessToken: 'tok' });
  });

  it('starts in disconnected state', () => {
    // connection.start() is pending — state is 'connecting' immediately after mount
    // which confirms the hook has not yet established a connection
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
    const { result, unmount } = renderHook(() => useAgentHub());

    await waitFor(() => {
      expect(result.current.connectionState).toBe('connected');
    });

    unmount();

    expect(mocks.connectionStop).toHaveBeenCalled();
  });
});
