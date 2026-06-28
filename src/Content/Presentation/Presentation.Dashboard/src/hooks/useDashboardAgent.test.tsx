import { describe, it, expect, beforeEach, vi } from 'vitest';
import { renderHook, act, waitFor } from '@testing-library/react';
import { of } from 'rxjs';
import { EventType } from '@ag-ui/core';

const createConversation = vi.fn(async () => 'thread-1');
const postToolResult = vi.fn(async () => {});
const runMock = vi.fn();
const createAuthenticatedAgUiAgent = vi.fn(async () => ({ run: runMock }));

vi.mock('@/lib/agUiClient', () => ({
  createConversation: (...args: unknown[]) => createConversation(...(args as [])),
  postToolResult: (...args: unknown[]) => postToolResult(...(args as [])),
  createAuthenticatedAgUiAgent: () => createAuthenticatedAgUiAgent(),
}));

const dispatchDashboardAction = vi.fn(async () => 'navigated to /spend');
vi.mock('@/lib/dashboardActions', () => ({
  dispatchDashboardAction: (...args: unknown[]) => dispatchDashboardAction(...(args as [])),
  describeAction: () => 'Navigating',
}));

const buildChart = vi.fn(async () => ({
  chart: { title: 'Tokens by Model', chartType: 'pie', unit: 'tokens', series: [{ labels: {}, dataPoints: [] }] },
  summary: 'Rendered a pie chart of "Tokens by Model" (1 series).',
}));
vi.mock('@/lib/chartRendering', () => ({
  buildChart: (...args: unknown[]) => buildChart(...(args as [])),
}));

import { useDashboardAgent } from './useDashboardAgent';
import { useChatStore } from '@/stores/chatStore';

function runWith(events: unknown[]) {
  runMock.mockReturnValue(of(...events));
}

describe('useDashboardAgent', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    useChatStore.setState({ open: true, threadId: null, messages: [], status: 'idle', error: null, toolActivity: null });
  });

  it('creates a conversation, dispatches a dashboard_control call, posts the result, and streams text', async () => {
    runWith([
      { type: EventType.RUN_STARTED, threadId: 'thread-1', runId: 'r1' },
      { type: EventType.TOOL_CALL_START, toolCallId: 'call-1', toolCallName: 'dashboard_control' },
      { type: EventType.TOOL_CALL_ARGS, toolCallId: 'call-1', delta: JSON.stringify({ operation: 'navigate', parameters: { path: '/spend' } }) },
      { type: EventType.TOOL_CALL_END, toolCallId: 'call-1' },
      { type: EventType.TEXT_MESSAGE_START, messageId: 'm1', role: 'assistant' },
      { type: EventType.TEXT_MESSAGE_CONTENT, messageId: 'm1', delta: 'Done.' },
      { type: EventType.TEXT_MESSAGE_END, messageId: 'm1' },
      { type: EventType.RUN_FINISHED, threadId: 'thread-1', runId: 'r1' },
    ]);

    const { result } = renderHook(() => useDashboardAgent());
    await act(async () => {
      await result.current.sendMessage('show spend');
    });

    expect(createConversation).toHaveBeenCalledTimes(1);
    expect(useChatStore.getState().threadId).toBe('thread-1');

    await waitFor(() => expect(dispatchDashboardAction).toHaveBeenCalledWith('navigate', { path: '/spend' }));
    await waitFor(() => expect(postToolResult).toHaveBeenCalledWith('thread-1', 'call-1', 'navigated to /spend'));

    const messages = useChatStore.getState().messages;
    expect(messages[0]).toMatchObject({ role: 'user', content: 'show spend' });
    expect(messages.some((m) => m.role === 'assistant' && m.content === 'Done.')).toBe(true);
    expect(useChatStore.getState().status).toBe('idle');
  });

  it('renders a chart on a render_chart call and appends a chart message', async () => {
    runWith([
      { type: EventType.TOOL_CALL_START, toolCallId: 'c2', toolCallName: 'render_chart' },
      { type: EventType.TOOL_CALL_ARGS, toolCallId: 'c2', delta: JSON.stringify({ metricId: 'tokens_by_model', chartType: 'pie' }) },
      { type: EventType.TOOL_CALL_END, toolCallId: 'c2' },
      { type: EventType.RUN_FINISHED, threadId: 'thread-1', runId: 'r1' },
    ]);

    const { result } = renderHook(() => useDashboardAgent());
    await act(async () => {
      await result.current.sendMessage('chart tokens by model');
    });

    await waitFor(() => expect(buildChart).toHaveBeenCalledWith({ metricId: 'tokens_by_model', chartType: 'pie' }));
    await waitFor(() =>
      expect(postToolResult).toHaveBeenCalledWith('thread-1', 'c2', 'Rendered a pie chart of "Tokens by Model" (1 series).'),
    );

    const chartMessage = useChatStore.getState().messages.find((m) => m.chart);
    expect(chartMessage?.chart?.chartType).toBe('pie');
  });

  it('reuses an existing threadId on a second turn', async () => {
    useChatStore.setState({ threadId: 'existing' });
    runWith([{ type: EventType.RUN_FINISHED, threadId: 'existing', runId: 'r1' }]);

    const { result } = renderHook(() => useDashboardAgent());
    await act(async () => {
      await result.current.sendMessage('hello');
    });

    expect(createConversation).not.toHaveBeenCalled();
  });

  it('surfaces a RUN_ERROR as an error status', async () => {
    runWith([{ type: EventType.RUN_ERROR, message: 'boom' }]);

    const { result } = renderHook(() => useDashboardAgent());
    await act(async () => {
      await result.current.sendMessage('go');
    });

    await waitFor(() => expect(useChatStore.getState().status).toBe('error'));
    expect(useChatStore.getState().error).toBe('boom');
  });

  it('ignores empty input and does not start a run', async () => {
    const { result } = renderHook(() => useDashboardAgent());
    await act(async () => {
      await result.current.sendMessage('   ');
    });
    expect(createAuthenticatedAgUiAgent).not.toHaveBeenCalled();
    expect(useChatStore.getState().messages).toHaveLength(0);
  });
});
