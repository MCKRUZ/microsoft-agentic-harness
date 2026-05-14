import { useRef } from 'react';
import { useMsal } from '@azure/msal-react';
import type { Subscription } from 'rxjs';
import { EventType } from '@ag-ui/core';
import type { BaseEvent, TextMessageContentEvent, TextMessageStartEvent, RunErrorEvent } from '@ag-ui/core';
import { createAuthenticatedAgUiAgent } from '@/lib/agUiClient';
import { loginRequest } from '@/lib/authConfig';
import { IS_AUTH_DISABLED } from '@/lib/devAuth';
import { useChatStore } from '@/stores/chatStore';

export interface UseAgentStreamReturn {
  sendMessage: (conversationId: string, userMessageId: string, message: string) => void;
  abort: () => void;
}

export function useAgentStream(): UseAgentStreamReturn {
  const { instance } = useMsal();
  const subscriptionRef = useRef<Subscription | null>(null);

  const getAccessToken = async (): Promise<string> => {
    if (IS_AUTH_DISABLED) return '';
    const account = instance.getAllAccounts()[0];
    if (!account) throw new Error('No account available');
    const result = await instance.acquireTokenSilent({ account, scopes: loginRequest.scopes });
    return result.accessToken;
  };

  const abort = (): void => {
    subscriptionRef.current?.unsubscribe();
    subscriptionRef.current = null;
  };

  const sendMessage = (conversationId: string, userMessageId: string, message: string): void => {
    abort();

    const chatStore = useChatStore.getState();
    chatStore.startStreaming();

    let currentMessageId: string | null = null;

    void createAuthenticatedAgUiAgent(getAccessToken).then((agent) => {
      const obs$ = agent.run({
        threadId: conversationId,
        runId: crypto.randomUUID(),
        messages: [
          {
            id: userMessageId,
            role: 'user',
            content: message,
          },
        ],
        tools: [],
        context: [],
        forwardedProps: {},
      });

      subscriptionRef.current = obs$.subscribe({
        next: (event: BaseEvent) => {
          switch (event.type) {
            case EventType.TEXT_MESSAGE_START: {
              const start = event as TextMessageStartEvent;
              currentMessageId = start.messageId;
              break;
            }
            case EventType.TEXT_MESSAGE_CONTENT: {
              const content = event as TextMessageContentEvent;
              useChatStore.getState().appendToken(content.delta);
              break;
            }
            case EventType.TEXT_MESSAGE_END: {
              const streamingContent = useChatStore.getState().streamingContent;
              useChatStore.getState().finalizeStream(streamingContent, currentMessageId ?? undefined);
              currentMessageId = null;
              break;
            }
            case EventType.RUN_ERROR: {
              const runError = event as RunErrorEvent;
              useChatStore.getState().setError(runError.message);
              break;
            }
            default:
              break;
          }
        },
        error: (err: unknown) => {
          const message = err instanceof Error ? err.message : 'Streaming error';
          useChatStore.getState().setError(message);
          subscriptionRef.current = null;
        },
        complete: () => {
          subscriptionRef.current = null;
        },
      });
    });
  };

  return { sendMessage, abort };
}
