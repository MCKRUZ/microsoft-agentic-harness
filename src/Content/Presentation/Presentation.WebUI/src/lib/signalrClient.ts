import { HubConnectionBuilder, LogLevel, type HubConnection } from '@microsoft/signalr';

export function buildHubConnection(
  path: string,
  getToken: () => Promise<string>,
): HubConnection {
  return new HubConnectionBuilder()
    .withUrl(path, { accessTokenFactory: getToken })
    .withAutomaticReconnect([0, 2000, 10000, 30000])
    .configureLogging(LogLevel.Warning)
    .build();
}
