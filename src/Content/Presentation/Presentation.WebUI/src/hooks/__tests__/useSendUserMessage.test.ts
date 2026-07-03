import { renderHook } from '@testing-library/react';
import { vi, describe, it, expect, beforeEach } from 'vitest';
import { useSendUserMessage } from '../useSendUserMessage';
import { useChatStore } from '@/stores/chatStore';
import { useAppStore } from '@/stores/appStore';

const agUiSend = vi.fn();
vi.mock('@/hooks/useAgentStream', () => ({
  useAgentStream: () => ({ sendMessage: agUiSend, abort: vi.fn() }),
}));

describe('useSendUserMessage', () => {
  beforeEach(() => {
    agUiSend.mockReset();
    useChatStore.getState().clearMessages();
    useAppStore.setState({ activeConversationId: null });
  });

  it('sends nothing and returns false when there is no active conversation', () => {
    const { result } = renderHook(() => useSendUserMessage());
    expect(result.current('hi')).toBe(false);
    expect(agUiSend).not.toHaveBeenCalled();
    expect(useChatStore.getState().messages).toHaveLength(0);
  });

  it('adds the user message, starts streaming, dispatches the run, and returns true', () => {
    useAppStore.setState({ activeConversationId: 'conv-1' });
    const { result } = renderHook(() => useSendUserMessage());

    expect(result.current('hello there')).toBe(true);

    const messages = useChatStore.getState().messages;
    expect(messages).toHaveLength(1);
    expect(messages[0]).toMatchObject({ role: 'user', content: 'hello there' });
    expect(useChatStore.getState().isStreaming).toBe(true);
    expect(agUiSend).toHaveBeenCalledWith('conv-1', expect.any(String), 'hello there');
  });
});
