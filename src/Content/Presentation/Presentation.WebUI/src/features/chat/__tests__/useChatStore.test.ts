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

  it('truncateFrom drops the named message and everything after it', () => {
    const store = useChatStore.getState();
    store.setMessages([
      { id: 'u1', role: 'user', content: 'first', timestamp: new Date() },
      { id: 'a1', role: 'assistant', content: 'reply', timestamp: new Date() },
      { id: 'u2', role: 'user', content: 'second', timestamp: new Date() },
    ]);
    store.truncateFrom('a1', 1);
    const state = useChatStore.getState();
    expect(state.messages).toHaveLength(1);
    expect(state.messages[0]?.id).toBe('u1');
  });

  it('truncateFrom cuts at the id even when the count disagrees with this list', () => {
    // #961: the list keeps only the newest 200 messages, and a failed turn can leave it a message out of step, so the
    // server's count is not a position in it. The id is.
    const store = useChatStore.getState();
    store.setMessages([
      { id: 'u1', role: 'user', content: 'first', timestamp: new Date() },
      { id: 'a1', role: 'assistant', content: 'reply', timestamp: new Date() },
      { id: 'u2', role: 'user', content: 'second', timestamp: new Date() },
      { id: 'a2', role: 'assistant', content: 'retried reply', timestamp: new Date() },
    ]);
    store.truncateFrom('a2', 250);
    expect(useChatStore.getState().messages.map((m) => m.id)).toEqual(['u1', 'a1', 'u2']);
  });

  it('truncateFrom with no id keeps every message', () => {
    // The server names no message when the one the request named was not in the conversation, so nothing was removed.
    const store = useChatStore.getState();
    store.setMessages([
      { id: 'u1', role: 'user', content: 'first', timestamp: new Date() },
      { id: 'a1', role: 'assistant', content: 'reply', timestamp: new Date() },
    ]);
    store.truncateFrom(null, 0);
    expect(useChatStore.getState().messages.map((m) => m.id)).toEqual(['u1', 'a1']);
  });

  it('truncateFrom falls back to the count when the named message is not shown', () => {
    const store = useChatStore.getState();
    store.setMessages([
      { id: 'u1', role: 'user', content: 'first', timestamp: new Date() },
      { id: 'a1', role: 'assistant', content: 'reply', timestamp: new Date() },
    ]);
    store.truncateFrom('not-shown', 1);
    expect(useChatStore.getState().messages.map((m) => m.id)).toEqual(['u1']);
  });

  it('truncateFrom re-inserts the pending edit message after truncation', () => {
    const store = useChatStore.getState();
    store.setMessages([
      { id: 'u1', role: 'user', content: 'old user', timestamp: new Date() },
      { id: 'a1', role: 'assistant', content: 'old reply', timestamp: new Date() },
    ]);
    // Simulate EditAndResubmit: client stages the edited user message, server truncates from the original.
    store.setPendingEditMessage({ id: 'u-new', role: 'user', content: 'edited user', timestamp: new Date() });
    store.truncateFrom('u1', 0);

    const state = useChatStore.getState();
    expect(state.messages).toHaveLength(1);
    expect(state.messages[0]?.id).toBe('u-new');
    expect(state.messages[0]?.content).toBe('edited user');
    expect(state.pendingEditMessage).toBeNull();
  });

  it('finalizeStream clears an edit that was never admitted', () => {
    // #964: an edit declined for a spent budget completes without HistoryTruncated, so nothing else would consume it.
    useChatStore.setState({
      pendingEditMessage: { id: 'u-new', role: 'user', content: 'declined edit', timestamp: new Date() },
    });

    useChatStore.getState().finalizeStream('budget spent', 'a1');

    expect(useChatStore.getState().pendingEditMessage).toBeNull();
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
