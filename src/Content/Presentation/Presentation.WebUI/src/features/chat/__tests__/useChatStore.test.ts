import { describe, it, expect, beforeEach } from 'vitest';
import { useChatStore } from '../useChatStore';

describe('useChatStore', () => {
  beforeEach(() => {
    useChatStore.setState({
      conversationId: null,
      messages: [],
      isStreaming: false,
      streamingContent: '',
      pendingEditMessage: null,
    });
  });

  it('appendToken accumulates tokens in streamingContent', () => {
    const { appendToken } = useChatStore.getState();
    appendToken('hello ');
    appendToken('world');
    const state = useChatStore.getState();
    expect(state.streamingContent).toBe('hello world');
    expect(state.isStreaming).toBe(true);
  });

  it('finalizeStream clears streamingContent and adds message to messages array', () => {
    const store = useChatStore.getState();
    store.appendToken('hello ');
    store.appendToken('world');
    store.finalizeStream('hello world');
    const state = useChatStore.getState();
    expect(state.streamingContent).toBe('');
    expect(state.isStreaming).toBe(false);
    expect(state.messages).toHaveLength(1);
    expect(state.messages[0]?.content).toBe('hello world');
    expect(state.messages[0]?.role).toBe('assistant');
  });

  it('truncateAfter drops messages beyond keepCount', () => {
    const store = useChatStore.getState();
    store.setMessages([
      { id: 'u1', role: 'user', content: 'first', timestamp: new Date() },
      { id: 'a1', role: 'assistant', content: 'reply', timestamp: new Date() },
      { id: 'u2', role: 'user', content: 'second', timestamp: new Date() },
    ]);
    store.truncateAfter(1);
    const state = useChatStore.getState();
    expect(state.messages).toHaveLength(1);
    expect(state.messages[0]?.id).toBe('u1');
  });

  it('truncateAfter re-inserts the pending edit message after truncation', () => {
    const store = useChatStore.getState();
    store.setMessages([
      { id: 'u1', role: 'user', content: 'old user', timestamp: new Date() },
      { id: 'a1', role: 'assistant', content: 'old reply', timestamp: new Date() },
    ]);
    // Simulate EditAndResubmit: client stages the edited user message, server truncates to keepCount=0.
    store.setPendingEditMessage({ id: 'u-new', role: 'user', content: 'edited user', timestamp: new Date() });
    store.truncateAfter(0);

    const state = useChatStore.getState();
    expect(state.messages).toHaveLength(1);
    expect(state.messages[0]?.id).toBe('u-new');
    expect(state.messages[0]?.content).toBe('edited user');
    expect(state.pendingEditMessage).toBeNull();
  });

  it('clearMessages resets all state', () => {
    const store = useChatStore.getState();
    store.addMessage({ id: '1', role: 'user', content: 'hi', timestamp: new Date() });
    store.appendToken('token');
    store.setConversationId('conv-1');
    store.clearMessages();
    const state = useChatStore.getState();
    expect(state.messages).toHaveLength(0);
    expect(state.isStreaming).toBe(false);
    expect(state.streamingContent).toBe('');
    expect(state.conversationId).toBe('conv-1');
  });
});
