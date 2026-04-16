import { render, screen } from '@testing-library/react';
import { describe, it, expect, beforeEach } from 'vitest';
import { useChatStore } from '../useChatStore';
import { TypingIndicator } from '../TypingIndicator';

describe('TypingIndicator', () => {
  beforeEach(() => {
    useChatStore.setState({ isStreaming: false, messages: [], streamingContent: '', conversationId: null });
  });

  it('is visible when isStreaming is true', () => {
    useChatStore.setState({ isStreaming: true });
    render(<TypingIndicator />);
    expect(screen.getByLabelText('Agent is typing')).toBeInTheDocument();
  });

  it('is not rendered when isStreaming is false', () => {
    render(<TypingIndicator />);
    expect(screen.queryByLabelText('Agent is typing')).not.toBeInTheDocument();
  });
});
