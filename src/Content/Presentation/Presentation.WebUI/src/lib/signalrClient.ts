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
    // Binding the element narrows it; the `<  length` comparison does not, because
    // noUncheckedIndexedAccess types every index access as possibly-undefined regardless of
    // what was proven about the bounds. Falling through to the steady-state delay is also
    // the correct behaviour if the lookup ever did miss.
    const staged = baseDelays[retryContext.previousRetryCount];
    if (staged !== undefined) return staged;
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
