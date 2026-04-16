import { render, screen, act } from '@testing-library/react';
import { describe, it, expect, beforeEach } from 'vitest';
import { useChatStore } from '../useChatStore';
import { MessageList } from '../MessageList';
import { TypingIndicator } from '../TypingIndicator';

const seedMessages = () => {
  useChatStore.getState().addMessage({ id: '1', role: 'user', content: 'Hello', timestamp: new Date() });
  useChatStore.getState().addMessage({ id: '2', role: 'assistant', content: 'Hi there', timestamp: new Date() });
  useChatStore.getState().addMessage({ id: '3', role: 'user', content: 'How are you?', timestamp: new Date() });
};

describe('MessageList', () => {
  beforeEach(() => {
    useChatStore.setState({
      conversationId: null,
      messages: [],
      isStreaming: false,
      streamingContent: '',
      error: null,
    });
  });

  it('renders all messages from the store', () => {
    seedMessages();
    render(<MessageList />);
    expect(screen.getByText('Hello')).toBeInTheDocument();
    expect(screen.getByText('Hi there')).toBeInTheDocument();
    expect(screen.getByText('How are you?')).toBeInTheDocument();
  });

  it('renders user message right-aligned (ml-auto)', () => {
    useChatStore.getState().addMessage({ id: '1', role: 'user', content: 'User msg', timestamp: new Date() });
    render(<MessageList />);
    const msgEl = screen.getByText('User msg');
    expect(msgEl.closest('[class*="ml-auto"]')).toBeInTheDocument();
  });

  it('renders assistant message left-aligned (mr-auto)', () => {
    useChatStore.getState().addMessage({ id: '1', role: 'assistant', content: 'Agent msg', timestamp: new Date() });
    render(<MessageList />);
    const msgEl = screen.getByText('Agent msg');
    expect(msgEl.closest('[class*="mr-auto"]')).toBeInTheDocument();
  });

  it('streaming: streamingContent updates visible text in DOM', () => {
    useChatStore.setState({ isStreaming: true, streamingContent: 'partial response' });
    render(<MessageList />);
    expect(screen.getByText('partial response')).toBeInTheDocument();
  });

  it('TurnComplete: finalizeStream hides streaming content and shows final message', () => {
    useChatStore.setState({ isStreaming: true, streamingContent: 'partial' });
    render(
      <>
        <MessageList />
        <TypingIndicator />
      </>,
    );
    expect(screen.getByText('partial')).toBeInTheDocument();
    expect(screen.getByLabelText('Agent is typing')).toBeInTheDocument();

    act(() => {
      useChatStore.getState().finalizeStream('Full response');
    });

    expect(screen.queryByLabelText('Agent is typing')).not.toBeInTheDocument();
    expect(screen.getByText('Full response')).toBeInTheDocument();
  });
});
