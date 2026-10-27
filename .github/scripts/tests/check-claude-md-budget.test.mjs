// Tests for check-claude-md-budget.mjs. Run: node --test .github/scripts/tests/
//
// The unit cases drive findProblems() with fake lookups. The CLI cases run the real script against
// the real CLAUDE.md and against broken copies of it, so they also prove the check can fail in the
// way CI relies on — a budget check that only ever passes would be a control that does nothing.

import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import { BUDGET_BYTES, byteLength, findProblems, isTestProjectPath, standingRules } from '../check-claude-md-budget.mjs';

const script = resolve(dirname(fileURLToPath(import.meta.url)), '..', 'check-claude-md-budget.mjs');
const realClaudeMd = resolve(dirname(script), '..', '..', 'CLAUDE.md');

const env = (overrides = {}) => ({
    ledgerIds: new Set(['inc-05']),
    rubricExists: true,
    testFilesFor: (name) => (name === 'RealGuardTests' ? ['src/Content/Tests/X.Tests/RealGuardTests.cs'] : []),
    ...overrides,
});
const doc = (...rules) => ['# P', '', '## Standing rules', 'Intro.', ...rules, '', '<!-- gitnexus:start -->', '- not a rule'].join('\n');
const kinds = (problems) => problems.map((p) => p.kind);

test('a file within budget whose pointers all resolve has no problems', () => {
    const text = doc('- a → INC-05', '- b → correctness-review-rubric', '- c → RealGuardTests');
    assert.deepEqual(findProblems(text, env()), []);
});

test('one byte over the budget fails, exactly at the budget passes', () => {
    const base = doc('- a → INC-05');
    const pad = (n) => base + 'x'.repeat(n - byteLength(base));
    assert.deepEqual(kinds(findProblems(pad(BUDGET_BYTES), env())), []);
    assert.deepEqual(kinds(findProblems(pad(BUDGET_BYTES + 1), env())), ['budget']);
});

test('bytes are counted as LF, so a CRLF checkout of a file at the budget still passes', () => {
    const base = doc('- a → INC-05');
    const lf = base + 'x'.repeat(BUDGET_BYTES - byteLength(base));
    assert.deepEqual(findProblems(lf.replace(/\n/g, '\r\n'), env()), []);
});

test('multi-byte characters count as bytes, not characters', () => {
    assert.equal(byteLength('→'), 3);
});

test('a guard name no test project mentions is a dead pointer', () => {
    assert.deepEqual(kinds(findProblems(doc('- a → NoSuchGuardTests'), env())), ['dead-pointer']);
});

test('an INC id with no ledger entry is a dead pointer', () => {
    assert.deepEqual(kinds(findProblems(doc('- a → INC-99'), env())), ['dead-pointer']);
});

test('a rubric pointer fails when the rubric file is gone', () => {
    assert.deepEqual(kinds(findProblems(doc('- a → correctness-review-rubric'), env({ rubricExists: false }))), ['dead-pointer']);
});

test('a standing rule with no arrow is reported', () => {
    assert.deepEqual(kinds(findProblems(doc('- a rule nothing checks'), env())), ['no-pointer']);
});

test('a target of no recognised shape is reported, not skipped', () => {
    assert.deepEqual(kinds(findProblems(doc('- a → see-the-wiki!'), env())), ['grammar']);
});

test('a missing Standing rules section is reported', () => {
    assert.deepEqual(kinds(findProblems('# P\n\nnothing here\n', env())), ['structure']);
});

// A guard name that survives only in a production XML comment ("see ValidateOnBuildSweepTests")
// must not count: that is how a deleted guard would keep passing.
test('only paths inside a test project count as a guard', () => {
    assert.equal(isTestProjectPath('src/Content/Tests/Presentation.Common.Tests/Composition/ValidateOnBuildSweepTests.cs'), true);
    assert.equal(isTestProjectPath('src/Content/Infrastructure/Infrastructure.AI.Governance/Adapters/CompositeResponseSanitizer.cs'), false);
    assert.equal(isTestProjectPath('src/Content/Application/Application.Core/ValidateOnBuildSweepTests.cs'), false);
});

test('bullets after the section ends are not treated as rules', () => {
    assert.deepEqual(standingRules(doc('- a → INC-05')).map((r) => r.text), ['- a → INC-05']);
});

const run = (args) => {
    try {
        return { code: 0, out: execFileSync('node', [script, ...args], { encoding: 'utf8' }) };
    } catch (error) {
        return { code: error.status, out: error.stdout };
    }
};
const brokenCopy = (mutate) => {
    const path = join(mkdtempSync(join(tmpdir(), 'claude-md-')), 'CLAUDE.md');
    writeFileSync(path, mutate(readFileSync(realClaudeMd, 'utf8')));
    return path;
};

test('CLI: the real CLAUDE.md passes', () => {
    const { code, out } = run([]);
    assert.equal(code, 0, out);
});

test('CLI: an oversized copy of the real CLAUDE.md fails on budget', () => {
    const { code, out } = run(['--claude-md', brokenCopy((t) => t + '\n' + 'x'.repeat(BUDGET_BYTES))]);
    assert.equal(code, 1);
    assert.match(out, /^BUDGET /m);
});

test('CLI: a bogus guard name in the real CLAUDE.md fails on the dead pointer', () => {
    const path = brokenCopy((t) => t.replace('→ PackageSkewTests', '→ PackageSkewTestsThatNoLongerExist'));
    const { code, out } = run(['--claude-md', path]);
    assert.equal(code, 1);
    assert.match(out, /DEAD-POINTER .*PackageSkewTestsThatNoLongerExist/);
});
