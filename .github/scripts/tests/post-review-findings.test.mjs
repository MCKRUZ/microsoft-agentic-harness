// Tests for post-review-findings.mjs: the marker contract, validation, and the two rules that
// make review converge — never repeat an open finding, and clear a finding that stopped
// appearing. Run: node --test .github/scripts/tests/
import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  buildMarker, parseMarker, validateFindings, buildCommentBody, planReview,
} from '../post-review-findings.mjs';

const sha = 'abcdef1234567890';
const f = (over = {}) => ({ path: 'src/A.cs', line: 10, class: 'null-deref', tag: 'important', title: 'Null reaches .Value', ...over });
const thread = (id, finding, { reviewer = 'correctness', isResolved = false } = {}) => ({
  id, isResolved, firstBody: buildCommentBody(reviewer, sha, finding),
});

test('a marker round-trips exactly (the telemetry and the next round depend on it)', () => {
  const marker = buildMarker({ reviewer: 'security', sha, path: 'src/Auth/X.cs', line: 7, cls: 'ownership-check-bypassed', tag: 'important' });
  assert.equal(marker, '<!-- rail:security sha=abcdef1 anchor=src/Auth/X.cs:7 class=ownership-check-bypassed tag=important -->');
  assert.deepEqual(parseMarker(`${marker}\n**[Important]** text`), {
    reviewer: 'security', sha: 'abcdef1', path: 'src/Auth/X.cs', line: 7, cls: 'ownership-check-bypassed', tag: 'important',
  });
});

test('a comment without a marker is not ours', () => {
  assert.equal(parseMarker('a human comment about src/A.cs:10'), null);
});

test('the visible text leads with the severity tag', () => {
  const body = buildCommentBody('correctness', sha, f({ tag: 'pre-existing', body: 'Fails when x is null.' }));
  assert.match(body.split('\n')[1], /^\*\*\[Pre-existing\]\*\* Null reaches \.Value `\(null-deref\)`$/);
  assert.match(body, /Fails when x is null\.$/);
});

test('invalid findings are rejected with reasons, valid ones kept', () => {
  const { valid, invalid } = validateFindings([f(), { path: 'src/A.cs', line: 0, class: 'Bad Class', tag: 'blocker', title: '' }]);
  assert.equal(valid.length, 1);
  assert.deepEqual(invalid[0].reasons, ['line', 'class (kebab-case)', 'tag (important|nit|pre-existing)', 'title']);
});

test('a non-array findings file yields nothing rather than throwing', () => {
  assert.deepEqual(validateFindings({ oops: true }), { valid: [], invalid: [] });
});

test('no repeats: an open thread for the same path and class is not posted again, even on a new line', () => {
  const plan = planReview('correctness', [thread('T1', f())], [f({ line: 14 })]);
  assert.equal(plan.toPost.length, 0);
  assert.equal(plan.repeats.length, 1);
  assert.equal(plan.toResolve.length, 0);
});

test('a resolved thread does not suppress a finding that came back', () => {
  const plan = planReview('correctness', [thread('T1', f(), { isResolved: true })], [f()]);
  assert.equal(plan.toPost.length, 1);
});

test('findings clear themselves: an open thread whose finding stopped appearing is resolved', () => {
  const plan = planReview('correctness', [thread('T1', f()), thread('T2', f({ class: 'off-by-one' }))], [f()]);
  assert.deepEqual(plan.toResolve.map((t) => t.id), ['T2']);
});

test("another reviewer's threads are never resolved or treated as repeats", () => {
  const plan = planReview('correctness', [thread('S1', f(), { reviewer: 'security' })], []);
  assert.equal(plan.toResolve.length, 0);
  const again = planReview('correctness', [thread('S1', f(), { reviewer: 'security' })], [f()]);
  assert.equal(again.toPost.length, 1);
});

test('the same class twice on one path becomes one thread (fix the class, not each line)', () => {
  const plan = planReview('correctness', [], [f({ line: 3 }), f({ line: 9 })]);
  assert.equal(plan.toPost.length, 1);
});
