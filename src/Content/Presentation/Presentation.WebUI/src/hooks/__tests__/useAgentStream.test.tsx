import { renderHook, act, waitFor } from '@testing-library/react';
import { Subject } from 'rxjs';
import { EventType } from '@ag-ui/core';
import { vi, describe, it, expect, beforeEach } from 'vitest';
import { useAgentStream } from '../useAgentStream';
import { useChatStore } from '@/stores/chatStore';

const postToolResult = vi.fn<(threadId: string, callId: string, result: string) => Promise<void>>();

vi.mock('@/lib/devAuth', () => ({ IS_AUTH_DISABLED: true }));
vi.mock('@azure/msal-react', () => ({ useMsal: () => ({ instance: {} }) }));
vi.mock('@/lib/authConfig', () => ({ loginRequest: { scopes: [] } }));
vi.mock('@/lib/agUiClient', () => ({
  createAuthenticatedAgUiAgent: vi.fn(),
  postToolResult: (threadId: string, callId: string, result: string) => postToolResult(threadId, callId, result),
}));

// Imported after the mock declaration; vi.mock is hoisted so this resolves to the mocked module.
import { createAuthenticatedAgUiAgent } from '@/lib/agUiClient';
const mockedCreate = vi.mocked(createAuthenticatedAgUiAgent);

/** Drives the hook, waits until it has subscribed to the run, then returns the event stream to feed. */
async function startRun(conversationId = 'conv-1'): Promise<Subject<unknown>> {
  const events$ = new Subject<unknown>();
  mockedCreate.mockResolvedValue({ run: () => events$ } as never);

  const { result } = renderHook(() => useAgentStream());
  act(() => { result.current.sendMessage(conversationId, 'user-1', 'show me a cat'); });
  // The subscribe happens after the createAuthenticatedAgUiAgent promise resolves; a Subject does not
  // replay, so events emitted before subscription would be lost. Wait for the subscription to attach.
  await waitFor(() => { expect(events$.observed).toBe(true); });
  return events$;
}

describe('useAgentStream — render_image round-trip', () => {
  beforeEach(() => {
    postToolResult.mockReset().mockResolvedValue(undefined);
    mockedCreate.mockReset();
    useChatStore.getState().clearMessages();
    useChatStore.getState().setConversationId('conv-1');
  });

  it('renders the image widget and posts a success result that unblocks the run', async () => {
    const events$ = await startRun();

    act(() => {
      events$.next({ type: EventType.TOOL_CALL_START, toolCallId: 'call-1', toolCallName: 'render_image' });
      events$.next({ type: EventType.TOOL_CALL_ARGS, toolCallId: 'call-1', delta: JSON.stringify({ url: 'https://example.com/cat.png', caption: 'Fluffy' }) });
      events$.next({ type: EventType.TOOL_CALL_END, toolCallId: 'call-1' });
    });

    await waitFor(() => {
      expect(postToolResult).toHaveBeenCalledWith('conv-1', 'call-1', expect.stringContaining('Displayed'));
    });

    const widgetMsg = useChatStore.getState().messages.find((m) => m.widget?.type === 'render_image');
    expect(widgetMsg?.widget?.args).toMatchObject({ url: 'https://example.com/cat.png', caption: 'Fluffy' });
  });

  it('accumulates argument deltas that arrive in multiple chunks', async () => {
    const events$ = await startRun();

    act(() => {
      events$.next({ type: EventType.TOOL_CALL_START, toolCallId: 'call-2', toolCallName: 'render_image' });
      events$.next({ type: EventType.TOOL_CALL_ARGS, toolCallId: 'call-2', delta: '{"url":"https://exa' });
      events$.next({ type: EventType.TOOL_CALL_ARGS, toolCallId: 'call-2', delta: 'mple.com/dog.png"}' });
      events$.next({ type: EventType.TOOL_CALL_END, toolCallId: 'call-2' });
    });

    await waitFor(() => { expect(postToolResult).toHaveBeenCalledWith('conv-1', 'call-2', expect.stringContaining('Displayed')); });
    expect(useChatStore.getState().messages.some((m) => m.widget?.args.url === 'https://example.com/dog.png')).toBe(true);
  });

  it('posts an explanatory result and renders no widget for a non-https url', async () => {
    const events$ = await startRun();

    act(() => {
      events$.next({ type: EventType.TOOL_CALL_START, toolCallId: 'call-3', toolCallName: 'render_image' });
      events$.next({ type: EventType.TOOL_CALL_ARGS, toolCallId: 'call-3', delta: JSON.stringify({ url: 'http://example.com/cat.png' }) });
      events$.next({ type: EventType.TOOL_CALL_END, toolCallId: 'call-3' });
    });

    await waitFor(() => { expect(postToolResult).toHaveBeenCalledWith('conv-1', 'call-3', expect.stringContaining('https')); });
    expect(useChatStore.getState().messages.some((m) => m.widget)).toBe(false);
  });

  it('still posts a result for a tool call it never saw a START for, so the server never hangs', async () => {
    const events$ = await startRun();

    act(() => { events$.next({ type: EventType.TOOL_CALL_END, toolCallId: 'orphan' }); });

    await waitFor(() => { expect(postToolResult).toHaveBeenCalledWith('conv-1', 'orphan', expect.stringContaining('No client handler')); });
  });
});
