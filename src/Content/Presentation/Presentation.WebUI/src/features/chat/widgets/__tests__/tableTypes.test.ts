import { describe, it, expect } from 'vitest';
import { parseTableArgs } from '../tableTypes';

describe('parseTableArgs', () => {
  it('accepts columns + rows and coerces cells to text', () => {
    const result = parseTableArgs({ title: 'Scores', columns: ['Name', 'Score'], rows: [['Ada', 97], ['Alan', true]] });
    expect(result).toEqual({
      ok: true,
      value: { title: 'Scores', columns: ['Name', 'Score'], rows: [['Ada', '97'], ['Alan', 'true']] },
    });
  });

  it('rejects a spec with no columns', () => {
    expect(parseTableArgs({ rows: [['x']] })).toEqual({ ok: false, reason: expect.stringContaining('columns') });
  });

  it('rejects an empty columns array', () => {
    expect(parseTableArgs({ columns: [] }).ok).toBe(false);
  });

  it('defaults rows to empty when absent', () => {
    const result = parseTableArgs({ columns: ['A'] });
    expect(result.ok && result.value.rows).toEqual([]);
  });

  it('treats an explicit null rows as empty (agents emit null for optional params)', () => {
    const result = parseTableArgs({ columns: ['A'], rows: null });
    expect(result.ok && result.value.rows).toEqual([]);
  });

  it('pads short rows and truncates long rows to the column count', () => {
    const result = parseTableArgs({ columns: ['A', 'B'], rows: [['only-a'], ['a', 'b', 'c']] });
    expect(result.ok && result.value.rows).toEqual([['only-a', ''], ['a', 'b']]);
  });

  it('drops non-array rows rather than throwing', () => {
    const result = parseTableArgs({ columns: ['A'], rows: ['flat', ['ok'], 42] });
    expect(result.ok && result.value.rows).toEqual([['ok']]);
  });

  it('coerces object/null cells to blank so no markup leaks through', () => {
    const result = parseTableArgs({ columns: ['A', 'B'], rows: [[{ nope: 1 }, null]] });
    expect(result.ok && result.value.rows).toEqual([['', '']]);
  });

  it('ignores a non-string title', () => {
    const result = parseTableArgs({ columns: ['A'], title: 42 });
    expect(result.ok && result.value.title).toBeUndefined();
  });
});
