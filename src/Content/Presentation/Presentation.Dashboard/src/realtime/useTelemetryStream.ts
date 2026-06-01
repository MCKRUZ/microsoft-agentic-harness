import { useEffect, useRef } from 'react';
import type { HubConnection } from '@microsoft/signalr';
import { buildHubConnection } from './signalrClient';
import { HUB_EVENTS, type HubEventName } from './eventTypes';
import { useTelemetryStore } from '@/stores/telemetryStore';
import { useSessionSnapshotsStore } from '@/stores/sessionSnapshotsStore';
import { IS_AUTH_DISABLED } from '@/auth/devAuth';
import { msalInstance, loginRequest } from '@/auth/authConfig';
import type { TelemetryEvent, ContextSnapshotEvent } from '@/api/types';

async function getToken(): Promise<string> {
  if (IS_AUTH_DISABLED) return '';
  const account = msalInstance.getAllAccounts()[0];
  if (!account) return '';
  const result = await msalInstance.acquireTokenSilent({ account, scopes: loginRequest.scopes });
  return result.accessToken;
}

// Module-scoped handle so non-React callers (session-detail page hooks in PR 4)
// can join the per-conversation Foresight observer group without re-creating
// a connection. Set by useTelemetryStream on mount, cleared on unmount.
let activeConnection: HubConnection | null = null;

/**
 * Joins the per-conversation Foresight observer group so this connection
 * starts receiving `ContextSnapshot` broadcasts for that conversation. Requires
 * the `AgentHub.Foresight.Observe` app role on the user. No-op when no live
 * connection exists yet (call again after `useTelemetryStream` mounts).
 */
export async function subscribeToConversationSnapshots(conversationId: string): Promise<void> {
  if (!activeConnection || activeConnection.state !== 'Connected') return;
  await activeConnection.invoke('SubscribeToConversationSnapshots', conversationId);
}

/** Pairs with {@link subscribeToConversationSnapshots}. */
export async function unsubscribeFromConversationSnapshots(conversationId: string): Promise<void> {
  if (!activeConnection || activeConnection.state !== 'Connected') return;
  await activeConnection.invoke('UnsubscribeFromConversationSnapshots', conversationId);
}

export function useTelemetryStream(): void {
  const connectionRef = useRef<HubConnection | null>(null);
  const push = useTelemetryStore((s) => s.push);
  const setConnected = useTelemetryStore((s) => s.setConnected);
  const appendSnapshot = useSessionSnapshotsStore((s) => s.appendSnapshot);

  useEffect(() => {
    const connection = buildHubConnection('/hubs/agent', getToken);
    connectionRef.current = connection;
    activeConnection = connection;

    const eventNames = Object.values(HUB_EVENTS) as HubEventName[];
    for (const eventName of eventNames) {
      connection.on(eventName, (data: Record<string, unknown>) => {
        // ContextSnapshot is routed exclusively to the per-session store —
        // the generic ring buffer would double-hold the largest event type
        // for no consumer benefit.
        if (eventName === HUB_EVENTS.ContextSnapshot) {
          appendSnapshot(data as unknown as ContextSnapshotEvent);
          return;
        }

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
      if (activeConnection === connection) activeConnection = null;
      connection.stop();
    };
  }, [push, setConnected, appendSnapshot]);
}
