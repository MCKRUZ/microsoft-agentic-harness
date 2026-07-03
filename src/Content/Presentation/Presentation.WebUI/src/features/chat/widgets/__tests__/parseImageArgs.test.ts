import { describe, it, expect } from 'vitest';
import { parseImageArgs } from '../types';

describe('parseImageArgs', () => {
  it('accepts an absolute https url and extracts optional alt/caption', () => {
    const result = parseImageArgs({ url: 'https://example.com/cat.png', alt: 'a cat', caption: 'Fluffy' });
    expect(result).toEqual({ ok: true, value: { url: 'https://example.com/cat.png', alt: 'a cat', caption: 'Fluffy' } });
  });

  it('trims surrounding whitespace on the url', () => {
    const result = parseImageArgs({ url: '  https://example.com/cat.png  ' });
    expect(result.ok && result.value.url).toBe('https://example.com/cat.png');
  });

  it('rejects a missing url', () => {
    expect(parseImageArgs({ alt: 'x' })).toEqual({ ok: false, reason: expect.stringContaining('url') });
  });

  it.each([
    ['http://example.com/cat.png', 'http (not https)'],
    ['javascript:alert(1)', 'javascript: scheme'],
    ['data:image/png;base64,AAAA', 'data: scheme'],
  ])('rejects non-https url %s (%s)', (url) => {
    const result = parseImageArgs({ url });
    expect(result.ok).toBe(false);
    expect(!result.ok && result.reason).toContain('https');
  });

  it('rejects an unparseable url', () => {
    const result = parseImageArgs({ url: 'not a url' });
    expect(result.ok).toBe(false);
  });

  it('ignores non-string alt/caption rather than passing them through', () => {
    const result = parseImageArgs({ url: 'https://example.com/x.png', alt: 42, caption: { nope: true } });
    expect(result.ok && result.value.alt).toBeUndefined();
    expect(result.ok && result.value.caption).toBeUndefined();
  });
});
