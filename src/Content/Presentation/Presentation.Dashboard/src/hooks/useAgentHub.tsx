import { useContext, createContext, useRef, useState, useEffect, type ReactNode } from 'react';
import { useMsal } from '@azure/msal-react';
import type { HubConnection } from '@microsoft/signalr';
import { buildHubConnection } from '@/realtime/signalrClient';
import { loginRequest } from '@/auth/authConfig';
import { IS_AUTH_DISABLED } from '@/auth/devAuth';
import { useChatStore } from '@/stores/chatStore';

export type ConnectionState = 'disconnected' | 'connecting' | 'connected' | 'reconnecting';

export interface ServerConversationMessage {
  id: string;
  role: string;
  content: string;
  timestamp: string;
  toolCalls?: { toolName: string; input: Record<string, unknown>; output: unknown }[] | null;
}

export interface ConversationSettingsInput {
  deploymentName: string | null;
  temperature: number | null;
  systemPromptOverride: string | null;
}

export interface UseAgentHubReturn {
  connectionState: ConnectionState;
  startConversation: (agentName: string, conversationId: string) => Promise<ServerConversationMessage[]>;
  invokeToolViaAgent: (conversationId: string, toolName: string, args: Record<string, unknown>) => Promise<void>;
  retryFromMessage: (conversationId: string, assistantMessageId: string) => Promise<void>;
  editAndResubmit: (
    conversationId: string,
    userMessageId: string,
    newUserMessageId: string,
    newContent: string,
  ) => Promise<void>;
  setConversationSettings: (conversationId: string, settings: ConversationSettingsInput) => Promise<void>;
}

const AgentHubContext = createContext<UseAgentHubReturn | null>(null);

export function AgentHubProvider({ children }: { children: ReactNode }) {
  const { instance } = useMsal();
  const [connectionState, setConnectionState] = useState<ConnectionState>('disconnected');
  const connectionRef = useRef<HubConnection | null>(null);

  useEffect(() => {
    let active = true;

    const getToken = async (): Promise<string> => {
      if (IS_AUTH_DISABLED) return '';
      const account = instance.getAllAccounts()[0];
      if (!account) throw new Error('No account available');
      const result = await instance.acquireTokenSilent({ account, scopes: loginRequest.scopes });
      return result.accessToken;
    };

    const connection = buildHubConnection('/hubs/agent', getToken);
    connectionRef.current = connection;

    // Streaming events are broadcast per hub connection, but a single client can
    // switch the active conversation mid-stream. Drop any payload whose
    // conversationId does not match the store's active conversation so tokens from
    // conversation A never render or finalize into conversation B's transcript.
    // When no conversation is active (conversationId === null) there is nothing to
    // contaminate, so the payload is allowed through.
    const isActiveConversation = (conversationId: string): boolean => {
      const active = useChatStore.getState().conversationId;
      return active === null || active === conversationId;
    };

    connection.on('TokenReceived', (payload: { conversationId: string; token: string; isComplete: boolean }) => {
      if (payload.isComplete) return;
      if (!isActiveConversation(payload.conversationId)) return;
      useChatStore.getState().appendToken(payload.token);
    });

    connection.on('TurnComplete', (payload: { conversationId: string; turnNumber: number; fullResponse: string; assistantMessageId?: string }) => {
      if (!isActiveConversation(payload.conversationId)) return;
      useChatStore.getState().finalizeStream(payload.fullResponse, payload.assistantMessageId);
    });

    connection.on('HistoryTruncated', (payload: { conversationId: string; keepCount: number }) => {
      if (!isActiveConversation(payload.conversationId)) return;
      useChatStore.getState().truncateAfter(payload.keepCount);
    });

    connection.on('Error', (payload: unknown) => {
      const message =
        typeof payload === 'string' ? payload
        : payload != null && typeof payload === 'object' && 'message' in payload && typeof (payload as Record<string, unknown>).message === 'string'
          ? (payload as Record<string, unknown>).message as string
          : JSON.stringify(payload);
      useChatStore.getState().setError(message);
    });

    connection.onreconnecting(() => { if (active) setConnectionState('reconnecting'); });
    connection.onreconnected(() => { if (active) setConnectionState('connected'); });
    connection.onclose(() => { if (active) setConnectionState('disconnected'); });

    // Effect synchronizes React state with an external system (the SignalR
    // connection lifecycle); driving connectionState as the connection
    // progresses is the intended use, not a render-cascade smell.
    // eslint-disable-next-line react-hooks/set-state-in-effect
    setConnectionState('connecting');
    const startPromise = connection.start()
      .then(async () => {
        if (!active) return;
        setConnectionState('connected');

        try {
          const res = await fetch('/api/config/auth');
          if (res.ok) {
            const { authDisabled: serverAuthDisabled } = await res.json();
            if (serverAuthDisabled !== IS_AUTH_DISABLED) {
              console.warn(
                `[AgentHub] Auth mismatch — client: auth ${IS_AUTH_DISABLED ? 'disabled' : 'enabled'}, ` +
                `server: auth ${serverAuthDisabled ? 'disabled' : 'enabled'}. ` +
                'Connection may fail. Check VITE_AZURE_SPA_CLIENT_ID and server Auth:Disabled config.',
              );
            }
          }
        } catch { /* server endpoint unavailable — non-fatal */ }
      })
      .catch((err: unknown) => {
        if (!active) return;
        setConnectionState('disconnected');
        const message = err instanceof Error ? err.message : 'SignalR connection failed';
        useChatStore.getState().setError(message);
      });

    return () => {
      active = false;
      connectionRef.current = null;
      // Stop only after start() settles. Calling stop() while negotiation is still
      // in flight — which StrictMode's dev double-mount guarantees — aborts it with
      // "The connection was stopped during negotiation". Chaining onto the start
      // promise lets the in-flight negotiation finish before we tear it down.
      void startPromise.finally(() => connection.stop());
    };
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const hubInvoke = <T = void>(method: string, ...args: unknown[]): Promise<T> => {
    const conn = connectionRef.current;
    if (!conn) return Promise.reject(new Error('SignalR connection not established'));
    return conn.invoke(method, ...args) as Promise<T>;
  };

  const value: UseAgentHubReturn = {
    connectionState,
    startConversation: (agentName, conversationId) =>
      hubInvoke<ServerConversationMessage[]>('StartConversation', agentName, conversationId),
    invokeToolViaAgent: (conversationId, toolName, args) =>
      hubInvoke('InvokeToolViaAgent', conversationId, toolName, JSON.stringify(args)),
    retryFromMessage: (conversationId, assistantMessageId) =>
      hubInvoke('RetryFromMessage', conversationId, assistantMessageId),
    editAndResubmit: (conversationId, userMessageId, newUserMessageId, newContent) =>
      hubInvoke('EditAndResubmit', conversationId, userMessageId, newUserMessageId, newContent),
    setConversationSettings: (conversationId, settings) =>
      hubInvoke('SetConversationSettings', conversationId, settings),
  };

  return <AgentHubContext value={value}>{children}</AgentHubContext>;
}

// Provider component and its consumer hook are intentionally colocated (the
// canonical React context pattern); Fast Refresh's component-only-export rule
// does not apply to a context module.
// eslint-disable-next-line react-refresh/only-export-components
export function useAgentHub(): UseAgentHubReturn {
  const ctx = useContext(AgentHubContext);
  if (!ctx) throw new Error('useAgentHub must be used within AgentHubProvider');
  return ctx;
}
