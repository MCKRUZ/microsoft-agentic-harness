import { describe, it, expect, vi } from 'vitest';
import { screen } from '@testing-library/react';
import axios from 'axios';
import { handlers } from './handlers';

vi.mock('@/lib/authConfig', () => ({
  loginRequest: { scopes: ['api://test-api/access_as_user'] },
}));

// SignalR mock pattern — class-based so `new HubConnectionBuilder()` works
const mockOn = vi.fn();
const mockInvoke = vi.fn().mockResolvedValue(undefined);
const mockStart = vi.fn().mockResolvedValue(undefined);
const mockStop = vi.fn().mockResolvedValue(undefined);
const mockBuild = vi.fn().mockReturnValue({
  start: mockStart,
  stop: mockStop,
  invoke: mockInvoke,
  on: mockOn,
  off: vi.fn(),
  onclose: vi.fn(),
  state: 'Connected',
});

vi.mock('@microsoft/signalr', () => {
  class MockHubConnectionBuilder {
    withUrl() { return this; }
    withAutomaticReconnect() { return this; }
    configureLogging() { return this; }
    build() { return mockBuild(); }
  }
  return {
    HubConnectionBuilder: MockHubConnectionBuilder,
    LogLevel: { Information: 1 },
    HubConnectionState: { Connected: 'Connected', Disconnected: 'Disconnected' },
  };
});

describe('renderWithProviders', () => {
  it('renders children without crashing', async () => {
    const { renderWithProviders } = await import('./utils');
    const { default: React } = await import('react');
    renderWithProviders(React.createElement('div', null, 'test'));
    expect(screen.getByText('test')).toBeInTheDocument();
  });
});

describe('MSW handlers', () => {
  it('returns expected fixtures for all /api/* routes', async () => {
    const routes = [
      { url: 'http://localhost/api/agents', key: 'name', expected: 'research-agent' },
      { url: 'http://localhost/api/mcp/tools', key: 'name', expected: 'get-time' },
      { url: 'http://localhost/api/mcp/resources', key: 'name', expected: 'Readme' },
      { url: 'http://localhost/api/mcp/prompts', key: 'name', expected: 'summarize' },
    ];

    for (const { url, key, expected } of routes) {
      const res = await axios.get<Record<string, string>[]>(url);
      expect(res.data[0]?.[key]).toBe(expected);
    }
  });

  it('exports handlers array with all expected routes', () => {
    expect(handlers.length).toBeGreaterThanOrEqual(5);
  });
});

describe('SignalR mock pattern', () => {
  it('HubConnectionBuilder mock captures registered event handlers', async () => {
    const signalr = await import('@microsoft/signalr');
    const builder = new signalr.HubConnectionBuilder();
    const conn = builder
      .withUrl('http://localhost/hubs/agent')
      .withAutomaticReconnect()
      .build();

    const handler = vi.fn();
    conn.on('TestEvent', handler);
    expect(mockOn).toHaveBeenCalledWith('TestEvent', handler);
  });
});
