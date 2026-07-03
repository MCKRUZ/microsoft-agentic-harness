import { describe, it, expect } from 'vitest';
import { parseFormArgs, formatSubmission, type FormSpec } from '../formTypes';

describe('parseFormArgs', () => {
  it('accepts a valid spec and carries title/submitLabel/required/options', () => {
    const result = parseFormArgs({
      title: 'Sign up',
      submitLabel: 'Go',
      fields: [
        { name: 'email', label: 'Email', type: 'text', required: true },
        { name: 'color', type: 'select', options: ['red', 'blue'] },
      ],
    });
    expect(result.ok).toBe(true);
    if (!result.ok) return;
    expect(result.value.title).toBe('Sign up');
    expect(result.value.submitLabel).toBe('Go');
    expect(result.value.fields).toHaveLength(2);
    expect(result.value.fields[0]).toMatchObject({ name: 'email', type: 'text', required: true });
    expect(result.value.fields[1].options).toEqual(['red', 'blue']);
  });

  it('rejects a spec with no fields', () => {
    expect(parseFormArgs({ title: 'x' }).ok).toBe(false);
    expect(parseFormArgs({ fields: [] }).ok).toBe(false);
  });

  it('drops fields with an unknown type', () => {
    const result = parseFormArgs({ fields: [{ name: 'a', type: 'text' }, { name: 'b', type: 'hologram' }] });
    expect(result.ok).toBe(true);
    if (result.ok) expect(result.value.fields.map((f) => f.name)).toEqual(['a']);
  });

  it('drops a select field that has no options', () => {
    const result = parseFormArgs({ fields: [{ name: 'a', type: 'text' }, { name: 'c', type: 'select' }] });
    expect(result.ok).toBe(true);
    if (result.ok) expect(result.value.fields.map((f) => f.name)).toEqual(['a']);
  });

  it('drops fields without a name', () => {
    const result = parseFormArgs({ fields: [{ type: 'text' }, { name: 'ok', type: 'text' }] });
    expect(result.ok).toBe(true);
    if (result.ok) expect(result.value.fields.map((f) => f.name)).toEqual(['ok']);
  });

  it('fails when every field is invalid', () => {
    expect(parseFormArgs({ fields: [{ name: 'a', type: 'bogus' }] }).ok).toBe(false);
  });

  it('drops duplicate field names, keeping the first', () => {
    const result = parseFormArgs({ fields: [{ name: 'x', type: 'text' }, { name: 'x', type: 'number' }] });
    expect(result.ok).toBe(true);
    if (result.ok) {
      expect(result.value.fields).toHaveLength(1);
      expect(result.value.fields[0].type).toBe('text');
    }
  });
});

describe('formatSubmission', () => {
  const spec: FormSpec = {
    fields: [
      { name: 'email', label: 'Email', type: 'text' },
      { name: 'news', label: 'Newsletter', type: 'checkbox' },
      { name: 'phone', label: 'Phone', type: 'text' },
    ],
  };

  it('renders labels and checkbox as Yes/No, omitting empty optional fields', () => {
    const message = formatSubmission(spec, { email: 'a@b.com', news: true, phone: '' });
    expect(message).toContain('Here are my answers:');
    expect(message).toContain('- Email: a@b.com');
    expect(message).toContain('- Newsletter: Yes');
    expect(message).not.toContain('Phone'); // empty optional omitted
  });

  it('falls back to the field name when no label is given', () => {
    const message = formatSubmission({ fields: [{ name: 'code', type: 'text' }] }, { code: 'X1' });
    expect(message).toContain('- code: X1');
  });
});
