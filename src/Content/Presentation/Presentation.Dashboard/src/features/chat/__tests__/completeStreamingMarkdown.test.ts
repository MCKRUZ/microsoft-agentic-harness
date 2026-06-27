import { describe, it, expect } from 'vitest';
import { completeStreamingMarkdown } from '../completeStreamingMarkdown';

describe('completeStreamingMarkdown', () => {
  it('closes a dangling backtick code fence so it parses as a code block now', () => {
    // The flicker case: opening fence has arrived, closing fence has not.
    const partial = 'Here is some code:\n```ts\nconst x = 1;';
    expect(completeStreamingMarkdown(partial)).toBe(
      'Here is some code:\n```ts\nconst x = 1;\n```',
    );
  });

  it('closes a dangling tilde fence with a tilde closer', () => {
    const partial = '~~~\nplain block';
    expect(completeStreamingMarkdown(partial)).toBe('~~~\nplain block\n~~~');
  });

  it('leaves a balanced code fence unchanged (no-op for completed content)', () => {
    const complete = 'before\n```ts\nconst x = 1;\n```\nafter';
    expect(completeStreamingMarkdown(complete)).toBe(complete);
  });

  it('does not append a separating newline when content already ends in one', () => {
    const partial = '```\ncode\n';
    expect(completeStreamingMarkdown(partial)).toBe('```\ncode\n```');
  });

  it('ignores triple backticks that appear mid-line in prose', () => {
    // Not a fence — it is not at the start of a line, so nothing is opened.
    const prose = 'Use the ``` syntax to start a block.';
    expect(completeStreamingMarkdown(prose)).toBe(prose);
  });

  it('treats an indented fence as a fence (leading whitespace allowed)', () => {
    const partial = '- item\n  ```\n  code';
    expect(completeStreamingMarkdown(partial)).toBe('- item\n  ```\n  code\n```');
  });

  it('does not treat a tilde close as closing a backtick fence', () => {
    // Different marker families do not pair — the backtick fence stays open.
    const partial = '```\ncode\n~~~';
    expect(completeStreamingMarkdown(partial)).toBe('```\ncode\n~~~\n```');
  });

  it('closes a 4-backtick fence with a 4-backtick closer (not a fixed 3)', () => {
    // A 4-marker fence (used when the code itself contains triple backticks) must
    // be closed by a run at least as long, or CommonMark would not close it.
    const partial = '````md\n```\nnested\n```';
    expect(completeStreamingMarkdown(partial)).toBe('````md\n```\nnested\n```\n````');
  });

  it('treats a shorter same-family fence inside a longer fence as content', () => {
    // Opened with 4 backticks; the inner 3-backtick line is content, so the block
    // stays open and is closed with 4 backticks.
    const partial = '````\n```';
    expect(completeStreamingMarkdown(partial)).toBe('````\n```\n````');
  });

  it('closes a longer fence opened after the standard three', () => {
    const partial = '`````python\nprint(1)';
    expect(completeStreamingMarkdown(partial)).toBe('`````python\nprint(1)\n`````');
  });

  it('returns empty string unchanged', () => {
    expect(completeStreamingMarkdown('')).toBe('');
  });

  it('leaves plain prose with no fences unchanged', () => {
    const text = 'Just a normal sentence with no code.';
    expect(completeStreamingMarkdown(text)).toBe(text);
  });
});
