import { useContext, createContext, useRef, useState, useEffect, type ReactNode } from 'react';
import { useMsal } from '@azure/msal-react';
import type { HubConnection } from '@microsoft/signalr';
import { buildHubConnection } from '@/lib/signalrClient';
import { loginRequest } from '@/lib/authConfig';
import { IS_AUTH_DISABLED } from '@/lib/devAuth';
import { useChatStore, type ChatMessage } from '@/stores/chatStore';

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
  editAndResubmit: (conversationId: string, userMessageId: string, newContent: string) => Promise<void>;
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

    connection.on('TokenReceived', (payload: { conversationId: string; token: string; isComplete: boolean }) => {
      if (payload.isComplete) return;
      useChatStore.getState().appendToken(payload.token);
    });

    connection.on('TurnComplete', (payload: { conversationId: string; turnNumber: number; fullResponse: string; assistantMessageId?: string }) => {
      useChatStore.getState().finalizeStream(payload.fullResponse, payload.assistantMessageId);
    });

    connection.on('HistoryTruncated', (payload: { conversationId: string; keepCount: number }) => {
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

    setConnectionState('connecting');
    connection.start()
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
      void connection.stop();
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
    editAndResubmit: (conversationId, userMessageId, newContent) =>
      hubInvoke('EditAndResubmit', conversationId, userMessageId, crypto.randomUUID(), newContent),
    setConversationSettings: (conversationId, settings) =>
      hubInvoke('SetConversationSettings', conversationId, settings),
  };

  return <AgentHubContext value={value}>{children}</AgentHubContext>;
}

export function useAgentHub(): UseAgentHubReturn {
  const ctx = useContext(AgentHubContext);
  if (!ctx) throw new Error('useAgentHub must be used within AgentHubProvider');
  return ctx;
}
