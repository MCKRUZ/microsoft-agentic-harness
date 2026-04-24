import { useEffect, useRef } from 'react';
import type { HubConnection } from '@microsoft/signalr';
import { buildHubConnection } from './signalrClient';
import { HUB_EVENTS, type HubEventName } from './eventTypes';
import { useTelemetryStore } from '@/stores/telemetryStore';
import { IS_AUTH_DISABLED } from '@/auth/devAuth';
import { msalInstance, loginRequest } from '@/auth/authConfig';
import type { TelemetryEvent } from '@/api/types';

async function getToken(): Promise<string> {
  if (IS_AUTH_DISABLED) return '';
  const account = msalInstance.getAllAccounts()[0];
  if (!account) return '';
  const result = await msalInstance.acquireTokenSilent({ account, scopes: loginRequest.scopes });
  return result.accessToken;
}

export function useTelemetryStream(): void {
  const connectionRef = useRef<HubConnection | null>(null);
  const push = useTelemetryStore((s) => s.push);
  const setConnected = useTelemetryStore((s) => s.setConnected);

  useEffect(() => {
    const connection = buildHubConnection('/hubs/agent', getToken);
    connectionRef.current = connection;

    const eventNames = Object.values(HUB_EVENTS) as HubEventName[];
    for (const eventName of eventNames) {
      connection.on(eventName, (data: Record<string, unknown>) => {
        const event: TelemetryEvent = {
          type: eventName,
          timestamp: Date.now(),
          data,
        };
        push(event);
      });
    }

    connection.onclose(() => setConnected(false));
    connection.onreconnected(() => setConnected(true));
    connection.onreconnecting(() => setConnected(false));

    connection.start()
      .then(() => setConnected(true))
      .catch(() => setConnected(false));

    return () => {
      connection.stop();
    };
  }, [push, setConnected]);
}
