import { useCallback, useEffect, useRef } from 'react';
import type { Subscription } from 'rxjs';
import type { BaseEvent } from '@ag-ui/core';
import { EventType } from '@ag-ui/core';
import {
  createAuthenticatedAgUiAgent,
  createConversation,
  postToolResult,
} from '@/lib/agUiClient';
import { describeAction, dispatchDashboardAction } from '@/lib/dashboardActions';
import { buildChart, type RenderChartParams } from '@/lib/chartRendering';
import { useChatStore } from '@/stores/chatStore';

/** Client round-trip tools the agent uses to act on the dashboard. */
const DASHBOARD_CONTROL = 'dashboard_control';
const RENDER_CHART = 'render_chart';

/** Accumulator for an in-flight tool call as its name and argument deltas stream in. */
interface PendingCall {
  name: string;
  args: string;
}

/** Executes a `dashboard_control` call and returns the result string the agent should observe. */
async function runDashboardControl(args: string): Promise<string> {
  try {
    const { operation, parameters } = JSON.parse(args || '{}') as {
      operation?: string;
      parameters?: Record<string, unknown>;
    };
    useChatStore.getState().setToolActivity(describeAction(operation ?? '', parameters ?? {}));
    return await dispatchDashboardAction(operation ?? '', parameters ?? {});
  } catch {
    return 'The dashboard could not interpret the requested action.';
  }
}

/** Executes a `render_chart` call: fetches the data, appends a chart message, and returns a summary. */
async function runRenderChart(args: string): Promise<string> {
  try {
    const params = JSON.parse(args || '{}') as RenderChartParams;
    useChatStore.getState().setToolActivity('Rendering chart');
    const { chart, summary } = await buildChart(params);
    useChatStore.getState().addMessage({ id: crypto.randomUUID(), role: 'assistant', content: summary, chart });
    return summary;
  } catch (e) {
    return e instanceof Error ? e.message : 'Could not render the chart.';
  }
}

/**
 * Drives an embedded dashboard-agent conversation over AG-UI. Exposes {@link sendMessage}; all
 * transcript and status state lives in the {@link useChatStore} so the panel can render it.
 *
 * The run uses the low-level {@link HttpAgent.run} observable (pure event transport) rather than the
 * managed loop, so the non-standard mid-run blocking proxy works cleanly: on `TOOL_CALL_END` the hook
 * executes the dashboard action locally and POSTs the result, which unblocks the server-side tool and
 * resumes the same run with the agent's text response.
 */
export function useDashboardAgent() {
  const subscriptionRef = useRef<Subscription | null>(null);
  const pendingCallsRef = useRef<Map<string, PendingCall>>(new Map());

  // Tear down any live run when the consuming component unmounts.
  useEffect(() => () => subscriptionRef.current?.unsubscribe(), []);

  const handleToolCallEnd = useCallback(async (threadId: string, callId: string) => {
    const pending = pendingCallsRef.current.get(callId);
    pendingCallsRef.current.delete(callId);
    if (!pending) return;

    let result: string;
    switch (pending.name) {
      case DASHBOARD_CONTROL:
        result = await runDashboardControl(pending.args);
        break;
      case RENDER_CHART:
        result = await runRenderChart(pending.args);
        break;
      default:
        result = `Unsupported client tool "${pending.name}".`;
        break;
    }

    // Always post a result — even on failure — so the awaiting server-side tool never hangs.
    await postToolResult(threadId, callId, result);
  }, []);

  const handleEvent = useCallback(
    (threadId: string, event: BaseEvent) => {
      const store = useChatStore.getState();
      switch (event.type) {
        case EventType.TEXT_MESSAGE_START: {
          const e = event as BaseEvent & { messageId: string };
          store.setToolActivity(null);
          store.addMessage({ id: e.messageId, role: 'assistant', content: '' });
          break;
        }
        case EventType.TEXT_MESSAGE_CONTENT: {
          const e = event as BaseEvent & { messageId: string; delta: string };
          store.appendToMessage(e.messageId, e.delta);
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
          void handleToolCallEnd(threadId, e.toolCallId);
          break;
        }
        case EventType.RUN_ERROR: {
          const e = event as BaseEvent & { message?: string };
          store.setToolActivity(null);
          store.setError(e.message ?? 'The agent run failed.');
          store.setStatus('error');
          break;
        }
        default:
          break;
      }
    },
    [handleToolCallEnd],
  );

  const sendMessage = useCallback(
    async (text: string) => {
      const trimmed = text.trim();
      const store = useChatStore.getState();
      if (!trimmed || store.status === 'running') return;

      store.setError(null);
      store.setStatus('running');
      store.addMessage({ id: crypto.randomUUID(), role: 'user', content: trimmed });

      try {
        let threadId = store.threadId;
        if (!threadId) {
          threadId = await createConversation();
          store.setThreadId(threadId);
        }

        const agent = await createAuthenticatedAgUiAgent();
        subscriptionRef.current?.unsubscribe();
        pendingCallsRef.current.clear();

        subscriptionRef.current = agent
          .run({
            threadId,
            runId: crypto.randomUUID(),
            state: {},
            messages: [{ id: crypto.randomUUID(), role: 'user', content: trimmed }],
            tools: [],
            context: [],
            forwardedProps: {},
          })
          .subscribe({
            next: (event) => handleEvent(threadId!, event),
            error: (err: unknown) => {
              const message = err instanceof Error ? err.message : 'The agent run failed.';
              useChatStore.getState().setToolActivity(null);
              useChatStore.getState().setError(message);
              useChatStore.getState().setStatus('error');
            },
            complete: () => {
              const s = useChatStore.getState();
              s.setToolActivity(null);
              if (s.status === 'running') s.setStatus('idle');
            },
          });
      } catch (err) {
        const message = err instanceof Error ? err.message : 'Could not start the agent.';
        store.setError(message);
        store.setStatus('error');
      }
    },
    [handleEvent],
  );

  return { sendMessage };
}
