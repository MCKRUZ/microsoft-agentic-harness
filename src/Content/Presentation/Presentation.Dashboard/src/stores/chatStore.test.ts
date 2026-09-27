import { describe, it, expect, beforeEach } from 'vitest';
import { useChatStore } from './chatStore';

describe('chatStore', () => {
  beforeEach(() => {
    useChatStore.setState({
      open: false,
      threadId: null,
      messages: [],
      status: 'idle',
      error: null,
      toolActivity: null,
      autoRoute: false,
      routedAgentName: null,
    });
  });

  it('toggles and sets the open flag', () => {
    useChatStore.getState().toggle();
    expect(useChatStore.getState().open).toBe(true);
    useChatStore.getState().setOpen(false);
    expect(useChatStore.getState().open).toBe(false);
  });

  it('adds messages in order', () => {
    const s = useChatStore.getState();
    s.addMessage({ id: 'a', role: 'user', content: 'hi' });
    s.addMessage({ id: 'b', role: 'assistant', content: '' });
    expect(useChatStore.getState().messages.map((m) => m.id)).toEqual(['a', 'b']);
  });

  it('appends streaming deltas to the matching message only', () => {
    const s = useChatStore.getState();
    s.addMessage({ id: 'b', role: 'assistant', content: '' });
    s.appendToMessage('b', 'Hello ');
    s.appendToMessage('b', 'world');
    s.appendToMessage('missing', 'ignored');
    expect(useChatStore.getState().messages).toEqual([{ id: 'b', role: 'assistant', content: 'Hello world' }]);
  });

  it('reset clears transcript and run state but keeps open and threadId', () => {
    useChatStore.setState({ open: true, threadId: 't1' });
    const s = useChatStore.getState();
    s.addMessage({ id: 'a', role: 'user', content: 'hi' });
    s.setStatus('error');
    s.setError('boom');
    s.setToolActivity('navigating');

    s.reset();

    const after = useChatStore.getState();
    expect(after.messages).toEqual([]);
    expect(after.status).toBe('idle');
    expect(after.error).toBeNull();
    expect(after.toolActivity).toBeNull();
    expect(after.open).toBe(true);
    expect(after.threadId).toBe('t1');
  });

  it('reset clears routedAgentName', () => {
    useChatStore.setState({ routedAgentName: 'research-agent' });
    useChatStore.getState().reset();
    expect(useChatStore.getState().routedAgentName).toBeNull();
  });

  it('sets autoRoute and routedAgentName independently', () => {
    const s = useChatStore.getState();
    s.setAutoRoute(true);
    expect(useChatStore.getState().autoRoute).toBe(true);
    s.setRoutedAgentName('research-agent');
    expect(useChatStore.getState().routedAgentName).toBe('research-agent');
  });
});
