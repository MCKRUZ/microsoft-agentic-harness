import { useRef, useState, useEffect } from 'react';
import { useMsal } from '@azure/msal-react';
import type { HubConnection } from '@microsoft/signalr';
import { buildHubConnection } from '@/lib/signalrClient';
import { loginRequest } from '@/lib/authConfig';
import { useChatStore, type ChatMessage } from '@/stores/chatStore';
import { useTelemetryStore } from '@/stores/telemetryStore';
import type { SpanData } from '@/types/signalr';

export type ConnectionState = 'disconnected' | 'connecting' | 'connected' | 'reconnecting';

export interface UseAgentHubReturn {
  connectionState: ConnectionState;
  sendMessage: (conversationId: string, message: string) => Promise<void>;
  startConversation: (agentName: string, conversationId: string) => Promise<void>;
  invokeToolViaAgent: (conversationId: string, toolName: string, args: Record<string, unknown>) => Promise<void>;
  joinGlobalTraces: () => Promise<void>;
  leaveGlobalTraces: () => Promise<void>;
}

export function useAgentHub(): UseAgentHubReturn {
  const { instance } = useMsal();
  const [connectionState, setConnectionState] = useState<ConnectionState>('disconnected');
  const connectionRef = useRef<HubConnection | null>(null);
  const stoppedRef = useRef(false);

  useEffect(() => {
    stoppedRef.current = false;

    // Resolve account at call time to avoid stale closure if user re-authenticates
    const getToken = async (): Promise<string> => {
      const account = instance.getAllAccounts()[0];
      if (!account) throw new Error('No account available');
      const result = await instance.acquireTokenSilent({ account, scopes: loginRequest.scopes });
      return result.accessToken;
    };

    const connection = buildHubConnection('/hubs/agent', getToken);
    connectionRef.current = connection;

    connection.on('TokenReceived', (token: string) => {
      useChatStore.getState().appendToken(token);
    });

    connection.on('TurnComplete', (message: { content: string }) => {
      useChatStore.getState().finalizeStream(message.content);
    });

    connection.on('Error', (message: string) => {
      useChatStore.getState().setError(message);
    });

    connection.on('SpanReceived', (span: SpanData) => {
      useTelemetryStore.getState().addConversationSpan(span.conversationId ?? '', span);
      useTelemetryStore.getState().addGlobalSpan(span);
    });

    connection.on('ConversationHistory', (messages: ChatMessage[]) => {
      useChatStore.getState().setMessages(messages);
    });

    connection.onreconnecting(() => { setConnectionState('reconnecting'); });
    connection.onreconnected(() => { setConnectionState('connected'); });
    connection.onclose(() => { setConnectionState('disconnected'); });

    setConnectionState('connecting');
    connection.start()
      .then(() => {
        if (!stoppedRef.current) setConnectionState('connected');
      })
      .catch((err: unknown) => {
        if (!stoppedRef.current) {
          setConnectionState('disconnected');
          const message = err instanceof Error ? err.message : 'SignalR connection failed';
          useChatStore.getState().setError(message);
        }
      });

    return () => {
      stoppedRef.current = true;
      connection.off('TokenReceived');
      connection.off('TurnComplete');
      connection.off('Error');
      connection.off('SpanReceived');
      connection.off('ConversationHistory');
      void connection.stop();
      connectionRef.current = null;
    };
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const sendMessage = async (conversationId: string, message: string): Promise<void> => {
    const conn = connectionRef.current;
    if (!conn) throw new Error('SignalR connection not established');
    await conn.invoke('SendMessage', conversationId, message);
  };

  const startConversation = async (agentName: string, conversationId: string): Promise<void> => {
    const conn = connectionRef.current;
    if (!conn) throw new Error('SignalR connection not established');
    await conn.invoke('StartConversation', agentName, conversationId);
  };

  const invokeToolViaAgent = async (
    conversationId: string,
    toolName: string,
    args: Record<string, unknown>,
  ): Promise<void> => {
    const conn = connectionRef.current;
    if (!conn) throw new Error('SignalR connection not established');
    await conn.invoke('InvokeToolViaAgent', conversationId, toolName, args);
  };

  const joinGlobalTraces = async (): Promise<void> => {
    const conn = connectionRef.current;
    if (!conn) throw new Error('SignalR connection not established');
    await conn.invoke('JoinGlobalTraces');
  };

  const leaveGlobalTraces = async (): Promise<void> => {
    const conn = connectionRef.current;
    if (!conn) throw new Error('SignalR connection not established');
    await conn.invoke('LeaveGlobalTraces');
  };

  return {
    connectionState,
    sendMessage,
    startConversation,
    invokeToolViaAgent,
    joinGlobalTraces,
    leaveGlobalTraces,
  };
}
