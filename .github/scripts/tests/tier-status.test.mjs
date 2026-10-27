// Tests for tier-status.mjs: what each tier needs before the `tier-gate` status goes green, and
// that a tier-2 approval covers exactly one commit and only counts when the owner wrote it.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { missingEvidence, findSignoff, decideStatus, signoffLine } from '../tier-status.mjs';

const head = '0123456789abcdef0123456789abcdef01234567';
const filled = `## Intent\nFix x.\n\n## Testing evidence\n\`dotnet test\` → Passed 40\n\n## Risk\nlow\n\n## How to revert\n\`gh pr revert 12\`\n\n## Explainer\n...\n\nReview-rounds: 1\n`;
const template = `## Intent\n\n<!-- what -->\n\n## Testing evidence\n\n<!-- paste output -->\n\n## Risk\n\n## How to revert\n\n<!-- command -->\n\nReview-rounds: <!-- n -->\n`;
const owner = 'MCKRUZ';
const signoff = (sha = head, login = owner) => ({ login, body: `Reviewed-by: @MCKRUZ ${sha} — read the evidence and the deep review` });

test('a filled-in template has no missing evidence', () => {
  assert.deepEqual(missingEvidence(filled), []);
});

test('the untouched template is missing both sections (HTML comments are not evidence)', () => {
  assert.deepEqual(missingEvidence(template), ['Testing evidence', 'How to revert']);
});

test('an empty or absent body is missing both', () => {
  assert.deepEqual(missingEvidence(''), ['Testing evidence', 'How to revert']);
  assert.deepEqual(missingEvidence(null), ['Testing evidence', 'How to revert']);
});

test('a section that is last in the body still counts when filled', () => {
  assert.deepEqual(missingEvidence('## Testing evidence\nran it: ok\n## How to revert\ngh pr revert 3'), []);
});

test('tier 0 is green with no body at all', () => {
  assert.equal(decideStatus({ tier: 0, body: '', comments: [], headSha: head, owner }).state, 'success');
});

test('tier 1 fails until the evidence is filled in, then passes', () => {
  assert.equal(decideStatus({ tier: 1, body: template, comments: [], headSha: head, owner }).state, 'failure');
  assert.equal(decideStatus({ tier: 1, body: filled, comments: [], headSha: head, owner }).state, 'success');
});

const summary = (sha = head, login = 'github-actions[bot]') => ({ login, body: `<!-- rail:deep-review-summary sha=${sha} -->\n2 findings: 1 Important, 1 Nit` });

test('tier 2 waits for the deep review, then for the sign-off', () => {
  const base = { tier: 2, body: filled, headSha: head, owner };
  assert.equal(decideStatus({ ...base, comments: [summary(), signoff()], deepReview: null }).state, 'pending');
  assert.equal(decideStatus({ ...base, comments: [summary(), signoff()], deepReview: 'in_progress' }).state, 'pending');
  assert.equal(decideStatus({ ...base, comments: [summary()], deepReview: 'success' }).state, 'pending');
  assert.equal(decideStatus({ ...base, comments: [summary(), signoff()], deepReview: 'success' }).state, 'success');
});

test('a passing deep-review job that reviewed nothing does not count (draft, or its own tier rules disagreed)', () => {
  const base = { tier: 2, body: filled, headSha: head, owner, deepReview: 'success' };
  assert.equal(decideStatus({ ...base, comments: [signoff()] }).state, 'pending');
  assert.equal(decideStatus({ ...base, comments: [summary('fedcba9876543210fedcba9876543210fedcba98'), signoff()] }).state, 'pending');
  assert.equal(decideStatus({ ...base, comments: [summary(head, 'MCKRUZ'), signoff()] }).state, 'pending');
});

test('tier 2 with empty evidence fails even when signed off', () => {
  assert.equal(decideStatus({ tier: 2, body: template, comments: [summary(), signoff()], headSha: head, owner, deepReview: 'success' }).state, 'failure');
});

test('an approval covers one commit: a sign-off naming an older commit does not count', () => {
  assert.equal(findSignoff([signoff('fedcba9876543210fedcba9876543210fedcba98')], head, owner), null);
});

test('a short commit prefix never counts — a prefix can be ground to carry the approval onto other code', () => {
  for (const n of [7, 12, 39]) assert.equal(findSignoff([signoff(head.slice(0, n))], head, owner), null, `${n} hex`);
});

test("only the owner's sign-off counts", () => {
  assert.equal(findSignoff([signoff(head, 'github-actions[bot]')], head, owner), null);
  assert.equal(findSignoff([signoff(head, 'someone-else')], head, owner), null);
});

test('a bare sign-off with no sentence about what was read does not count', () => {
  assert.equal(findSignoff([{ login: owner, body: `Reviewed-by: @MCKRUZ ${head}` }], head, owner), null);
});

test('a plain hyphen and any login case are accepted', () => {
  assert.ok(findSignoff([{ login: 'mckruz', body: `Reviewed-by: @MCKRUZ ${head} - read it` }], head, owner));
});

test('when the PR edits the deep review, the gate stops waiting for it but still needs the sign-off', () => {
  const base = { tier: 2, body: filled, headSha: head, owner, deepReview: null, deepReviewTrusted: false };
  const waiting = decideStatus({ ...base, comments: [] });
  assert.equal(waiting.state, 'pending');
  assert.match(waiting.description, /FULL diff/);
  assert.equal(decideStatus({ ...base, comments: [signoff()] }).state, 'success');
});

test('the copy-paste sign-off line names the full commit id', () => {
  assert.match(signoffLine('MCKRUZ', head.toUpperCase()), new RegExp(`^Reviewed-by: @MCKRUZ ${head} — `));
});

test('every description fits the 140-character status limit', () => {
  for (const d of [
    decideStatus({ tier: 2, body: filled, comments: [], headSha: head, owner: 'a-very-long-github-login-name-x', deepReview: 'success' }),
    decideStatus({ tier: 2, body: template, comments: [], headSha: head, owner }),
  ]) assert.ok(d.description.length <= 140, d.description);
});
