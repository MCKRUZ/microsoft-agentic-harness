import { useRef, useState, useEffect } from 'react';
import { useMsal } from '@azure/msal-react';
import type { HubConnection } from '@microsoft/signalr';
import { buildHubConnection } from '@/lib/signalrClient';
import { loginRequest } from '@/lib/authConfig';
import { IS_AUTH_DISABLED } from '@/lib/devAuth';
import { useChatStore, type ChatMessage } from '@/stores/chatStore';
import { useTelemetryStore } from '@/stores/telemetryStore';
import type { SpanData } from '@/types/signalr';

export type ConnectionState = 'disconnected' | 'connecting' | 'connected' | 'reconnecting';

export interface ConversationSettingsInput {
  deploymentName: string | null;
  temperature: number | null;
  systemPromptOverride: string | null;
}

export interface UseAgentHubReturn {
  connectionState: ConnectionState;
  sendMessage: (conversationId: string, userMessageId: string, message: string) => Promise<void>;
  startConversation: (agentName: string, conversationId: string) => Promise<void>;
  invokeToolViaAgent: (conversationId: string, toolName: string, args: Record<string, unknown>) => Promise<void>;
  retryFromMessage: (conversationId: string, assistantMessageId: string) => Promise<void>;
  editAndResubmit: (conversationId: string, userMessageId: string, newContent: string) => Promise<void>;
  setConversationSettings: (conversationId: string, settings: ConversationSettingsInput) => Promise<void>;
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

    connection.on('SpanReceived', (span: SpanData) => {
      useTelemetryStore.getState().addSpan(span);
    });

    connection.on('ConversationHistory', (messages: ChatMessage[]) => {
      useChatStore.getState().setMessages(messages);
    });

    connection.onreconnecting(() => { setConnectionState('reconnecting'); });
    connection.onreconnected(() => { setConnectionState('connected'); });
    connection.onclose(() => { setConnectionState('disconnected'); });

    setConnectionState('connecting');
    // Track start() so cleanup can wait for it to settle before calling stop().
    // Calling stop() mid-negotiate (e.g. React StrictMode's synchronous remount)
    // triggers a "stopped during negotiation" warning from the SignalR client.
    const startPromise = connection.start()
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
      connection.off('HistoryTruncated');
      connection.off('Error');
      connection.off('SpanReceived');
      connection.off('ConversationHistory');
      void startPromise.finally(() => connection.stop());
      connectionRef.current = null;
    };
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const hubInvoke = (method: string, ...args: unknown[]): Promise<void> => {
    const conn = connectionRef.current;
    if (!conn) throw new Error('SignalR connection not established');
    return conn.invoke(method, ...args) as Promise<void>;
  };

  const sendMessage = (conversationId: string, userMessageId: string, message: string): Promise<void> =>
    hubInvoke('SendMessage', conversationId, userMessageId, message);

  const startConversation = (agentName: string, conversationId: string): Promise<void> =>
    hubInvoke('StartConversation', agentName, conversationId);

  const invokeToolViaAgent = (
    conversationId: string,
    toolName: string,
    args: Record<string, unknown>,
  ): Promise<void> => hubInvoke('InvokeToolViaAgent', conversationId, toolName, args);

  const retryFromMessage = (conversationId: string, assistantMessageId: string): Promise<void> =>
    hubInvoke('RetryFromMessage', conversationId, assistantMessageId);

  const editAndResubmit = (
    conversationId: string,
    userMessageId: string,
    newContent: string,
  ): Promise<void> =>
    hubInvoke('EditAndResubmit', conversationId, userMessageId, crypto.randomUUID(), newContent);

  const setConversationSettings = (
    conversationId: string,
    settings: ConversationSettingsInput,
  ): Promise<void> => hubInvoke('SetConversationSettings', conversationId, settings);

  const joinGlobalTraces = (): Promise<void> => hubInvoke('JoinGlobalTraces');

  const leaveGlobalTraces = (): Promise<void> => hubInvoke('LeaveGlobalTraces');

  return {
    connectionState,
    sendMessage,
    startConversation,
    invokeToolViaAgent,
    retryFromMessage,
    editAndResubmit,
    setConversationSettings,
    joinGlobalTraces,
    leaveGlobalTraces,
  };
}
