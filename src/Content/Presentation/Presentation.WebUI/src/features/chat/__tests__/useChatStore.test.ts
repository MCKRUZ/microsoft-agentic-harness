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
    store.truncateFrom('a1');
    const state = useChatStore.getState();
    expect(state.messages).toHaveLength(1);
    expect(state.messages[0]?.id).toBe('u1');
  });

  it('truncateFrom cuts at the id, whatever the list holds before it', () => {
    // #961: the list keeps only the newest 200 messages, so the server's count is not a position in it. The id is.
    const store = useChatStore.getState();
    store.setMessages([
      { id: 'u1', role: 'user', content: 'first', timestamp: new Date() },
      { id: 'a1', role: 'assistant', content: 'reply', timestamp: new Date() },
      { id: 'u2', role: 'user', content: 'second', timestamp: new Date() },
      { id: 'a2', role: 'assistant', content: 'retried reply', timestamp: new Date() },
    ]);
    store.truncateFrom('a2');
    expect(useChatStore.getState().messages.map((m) => m.id)).toEqual(['u1', 'a1', 'u2']);
  });

  it('truncateFrom with no id keeps every message', () => {
    // The server names no message when the one the request named was not in the conversation, so nothing was removed.
    const store = useChatStore.getState();
    store.setMessages([
      { id: 'u1', role: 'user', content: 'first', timestamp: new Date() },
      { id: 'a1', role: 'assistant', content: 'reply', timestamp: new Date() },
    ]);
    store.truncateFrom(null);
    expect(useChatStore.getState().messages.map((m) => m.id)).toEqual(['u1', 'a1']);
  });

  it('truncateFrom drops every shown message when the named one is older than the window', () => {
    // A retry from the oldest shown reply removes the user message just before it, which the 200-message window may
    // already have dropped. Everything shown comes after that message, so all of it goes; a count would keep it all.
    const store = useChatStore.getState();
    store.setMessages([
      { id: 'a1', role: 'assistant', content: 'oldest shown reply', timestamp: new Date() },
      { id: 'u2', role: 'user', content: 'second', timestamp: new Date() },
    ]);
    store.setPendingEditMessage({ id: 'u-new', role: 'user', content: 'edited', timestamp: new Date() });
    store.truncateFrom('windowed-out');
    const state = useChatStore.getState();
    expect(state.messages.map((m) => m.id)).toEqual(['u-new']);
    expect(state.pendingEditMessage).toBeNull();
  });

  it('truncateFrom re-inserts the pending edit message after truncation', () => {
    const store = useChatStore.getState();
    store.setMessages([
      { id: 'u1', role: 'user', content: 'old user', timestamp: new Date() },
      { id: 'a1', role: 'assistant', content: 'old reply', timestamp: new Date() },
    ]);
    // Simulate EditAndResubmit: client stages the edited user message, server truncates from the original.
    store.setPendingEditMessage({ id: 'u-new', role: 'user', content: 'edited user', timestamp: new Date() });
    store.truncateFrom('u1');

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
