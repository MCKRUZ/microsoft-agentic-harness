import {
  HubConnectionBuilder,
  LogLevel,
  type HubConnection,
  type IRetryPolicy,
  type RetryContext,
} from '@microsoft/signalr';

const infiniteRetryPolicy: IRetryPolicy = {
  nextRetryDelayInMilliseconds(retryContext: RetryContext): number | null {
    const baseDelays = [0, 2000, 4000, 8000, 16000];
    if (retryContext.previousRetryCount < baseDelays.length)
      return baseDelays[retryContext.previousRetryCount];
    return 30_000 + Math.random() * 5000;
  },
};

export function buildHubConnection(
  path: string,
  getToken: () => Promise<string>,
): HubConnection {
  const connection = new HubConnectionBuilder()
    .withUrl(path, { accessTokenFactory: getToken })
    .withAutomaticReconnect(infiniteRetryPolicy)
    .configureLogging(LogLevel.Warning)
    .build();

  connection.serverTimeoutInMilliseconds = 120_000;
  connection.keepAliveIntervalInMilliseconds = 30_000;
  return connection;
}
