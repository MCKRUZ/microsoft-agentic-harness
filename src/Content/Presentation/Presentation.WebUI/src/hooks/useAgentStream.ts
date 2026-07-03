import { useRef } from 'react';
import { useMsal } from '@azure/msal-react';
import type { Subscription } from 'rxjs';
import { EventType } from '@ag-ui/core';
import type { BaseEvent, TextMessageContentEvent, TextMessageStartEvent, RunErrorEvent } from '@ag-ui/core';
import { createAuthenticatedAgUiAgent, postToolResult } from '@/lib/agUiClient';
import { getWidget } from '@/features/chat/widgets/registry';
import type { AgentWidget } from '@/features/chat/widgets/types';
import { loginRequest } from '@/lib/authConfig';
import { IS_AUTH_DISABLED } from '@/lib/devAuth';
import { useChatStore } from '@/stores/chatStore';

export interface UseAgentStreamReturn {
  sendMessage: (conversationId: string, userMessageId: string, message: string) => void;
  abort: () => void;
}

/** Accumulator for an in-flight client tool call as its name and argument deltas stream in. */
interface PendingCall {
  name: string;
  args: string;
}

/**
 * Runs a completed client widget tool call: validates the agent's args at the client trust boundary,
 * renders the widget as an assistant message on success, and returns the acknowledgement the agent
 * observes. Everything widget-specific (validation, component, ack text) lives in the widget registry,
 * so this stays generic across all widgets and is unchanged when a new one is added. On invalid args it
 * returns the validator's reason and renders nothing, so the agent can recover.
 */
function runWidgetToolCall(name: string, argsJson: string): string {
  const widget = getWidget(name);
  if (!widget) return `The client has no handler for widget "${name}".`;

  let args: Record<string, unknown>;
  try {
    args = JSON.parse(argsJson || '{}') as Record<string, unknown>;
  } catch {
    return `The ${name} arguments could not be parsed.`;
  }

  const check = widget.validate(args);
  if (!check.ok) return check.reason;

  const rendered: AgentWidget = { type: name, args };
  useChatStore.getState().addMessage({
    id: crypto.randomUUID(),
    role: 'assistant',
    content: '',
    timestamp: new Date(),
    widget: rendered,
  });
  return widget.ack;
}

/**
 * Completes a mid-run client tool call: computes a result for the call (rendering its widget as a side
 * effect) and POSTs it back so the parked server-side tool unblocks and the run resumes. A result is
 * posted for every callId — including one with no matching START — so the awaiting server tool never
 * hangs. A failed POST surfaces an error rather than leaving the run stuck. Widget dispatch is generic
 * over the widget registry, so adding a widget touches only widgets/registry.tsx.
 */
async function finishToolCall(threadId: string, callId: string, pending: PendingCall | undefined): Promise<void> {
  const result = pending
    ? runWidgetToolCall(pending.name, pending.args)
    : `No client handler matched tool call ${callId}.`;

  try {
    await postToolResult(threadId, callId, result);
  } catch (err) {
    const message = err instanceof Error ? err.message : 'Could not deliver the tool result to the agent.';
    useChatStore.getState().setError(message);
  }
}

export function useAgentStream(): UseAgentStreamReturn {
  const { instance } = useMsal();
  const subscriptionRef = useRef<Subscription | null>(null);
  const pendingCallsRef = useRef<Map<string, PendingCall>>(new Map());

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
    pendingCallsRef.current.clear();
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
            case EventType.TOOL_CALL_START: {
              const e = event as BaseEvent & { toolCallId: string; toolCallName: string };
              pendingCallsRef.current.set(e.toolCallId, { name: e.toolCallName, args: '' });
              break;
            }
            case EventType.TOOL_CALL_ARGS: {
              const e = event as BaseEvent & { toolCallId: string; delta: string };
              const pending = pendingCallsRef.current.get(e.toolCallId);
              if (pending) pending.args += e.delta;
              break;
            }
            case EventType.TOOL_CALL_END: {
              const e = event as BaseEvent & { toolCallId: string };
              const pending = pendingCallsRef.current.get(e.toolCallId);
              pendingCallsRef.current.delete(e.toolCallId);
              // The server tool is parked awaiting this result; fire-and-forget the round-trip so the
              // same open run resumes with the widget rendered and the agent's follow-up narration.
              void finishToolCall(conversationId, e.toolCallId, pending);
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
