import { vi, describe, it, expect } from 'vitest';
import { buildHubConnection } from '../signalrClient';

const mocks = vi.hoisted(() => {
  const build = vi.fn(() => ({}));
  const withUrl = vi.fn();
  const withAutomaticReconnect = vi.fn();
  const configureLogging = vi.fn();

  const builder = { withUrl, withAutomaticReconnect, configureLogging, build };

  withUrl.mockReturnValue(builder);
  withAutomaticReconnect.mockReturnValue(builder);
  configureLogging.mockReturnValue(builder);

  return { build, withUrl, withAutomaticReconnect, configureLogging };
});

vi.mock('@microsoft/signalr', () => ({
  // Must use 'function' (not arrow) so 'new HubConnectionBuilder()' works
  HubConnectionBuilder: vi.fn(function () {
    return {
      withUrl: mocks.withUrl,
      withAutomaticReconnect: mocks.withAutomaticReconnect,
      configureLogging: mocks.configureLogging,
      build: mocks.build,
    };
  }),
  LogLevel: { Warning: 2 },
}));

describe('buildHubConnection', () => {
  it('creates connection with accessTokenFactory', () => {
    const getToken = async () => 'tok';

    buildHubConnection('/hubs/agent', getToken);

    expect(mocks.withUrl).toHaveBeenCalledWith(
      '/hubs/agent',
      expect.objectContaining({ accessTokenFactory: getToken }),
    );
  });
});
