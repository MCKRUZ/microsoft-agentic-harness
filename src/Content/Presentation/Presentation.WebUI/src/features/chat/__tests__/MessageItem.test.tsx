import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { vi, describe, it, expect } from 'vitest';
import { MessageItem } from '../MessageItem';
import type { ChatMessage } from '../useChatStore';

const userMsg: ChatMessage = {
  id: 'user-1',
  role: 'user',
  content: 'original text',
  timestamp: new Date(),
};

const assistantMsg: ChatMessage = {
  id: 'asst-1',
  role: 'assistant',
  content: 'agent reply',
  timestamp: new Date(),
};

describe('MessageItem retry/edit actions', () => {
  it('Retry button on assistant message invokes onRetry with message id', async () => {
    const onRetry = vi.fn();
    const user = userEvent.setup();
    render(<MessageItem message={assistantMsg} onRetry={onRetry} />);
    await user.click(screen.getByRole('button', { name: /regenerate response/i }));
    expect(onRetry).toHaveBeenCalledWith('asst-1');
  });

  it('does not render Retry on user messages', () => {
    render(<MessageItem message={userMsg} onRetry={vi.fn()} />);
    expect(screen.queryByRole('button', { name: /regenerate response/i })).not.toBeInTheDocument();
  });

  it('Edit button on user message opens inline editor with current content', async () => {
    const user = userEvent.setup();
    render(<MessageItem message={userMsg} onEdit={vi.fn()} />);
    await user.click(screen.getByRole('button', { name: /edit message/i }));
    expect(screen.getByRole('textbox', { name: /edit message/i })).toHaveValue('original text');
  });

  it('Save in editor calls onEdit with id and new content', async () => {
    const onEdit = vi.fn();
    const user = userEvent.setup();
    render(<MessageItem message={userMsg} onEdit={onEdit} />);
    await user.click(screen.getByRole('button', { name: /edit message/i }));
    const textarea = screen.getByRole('textbox', { name: /edit message/i });
    await user.clear(textarea);
    await user.type(textarea, 'revised text');
    await user.click(screen.getByRole('button', { name: /save/i }));
    expect(onEdit).toHaveBeenCalledWith('user-1', 'revised text');
  });

  it('Cancel in editor reverts draft and does not call onEdit', async () => {
    const onEdit = vi.fn();
    const user = userEvent.setup();
    render(<MessageItem message={userMsg} onEdit={onEdit} />);
    await user.click(screen.getByRole('button', { name: /edit message/i }));
    const textarea = screen.getByRole('textbox', { name: /edit message/i });
    await user.clear(textarea);
    await user.type(textarea, 'scratch');
    await user.click(screen.getByRole('button', { name: /cancel/i }));
    expect(onEdit).not.toHaveBeenCalled();
    expect(screen.getByText('original text')).toBeInTheDocument();
  });

  it('Save with unchanged content is a no-op (closes editor without invoking onEdit)', async () => {
    const onEdit = vi.fn();
    const user = userEvent.setup();
    render(<MessageItem message={userMsg} onEdit={onEdit} />);
    await user.click(screen.getByRole('button', { name: /edit message/i }));
    await user.click(screen.getByRole('button', { name: /save/i }));
    expect(onEdit).not.toHaveBeenCalled();
    expect(screen.queryByRole('textbox', { name: /edit message/i })).not.toBeInTheDocument();
  });

  it('disabled hides the action row entirely', () => {
    render(<MessageItem message={assistantMsg} onRetry={vi.fn()} disabled />);
    expect(screen.queryByRole('button', { name: /regenerate response/i })).not.toBeInTheDocument();
  });

  it('isStreaming hides Retry (cannot retry an in-flight response)', () => {
    render(<MessageItem message={assistantMsg} onRetry={vi.fn()} isStreaming />);
    expect(screen.queryByRole('button', { name: /regenerate response/i })).not.toBeInTheDocument();
  });
});
