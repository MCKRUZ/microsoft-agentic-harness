// node --test scripts/rails/tests/*.test.mjs
import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  NO_DATA, isReviewFixSubject, isRevertSubject, parseDisposition, parseFindingMarker, parseReviewRounds,
  parseReviewedMinutes, parseSeverityTag, parseSignoff, tierFromLabels, isConventionComment,
} from '../lib/review-parsers.mjs';
import { botCommentKind, classifyCorrectness, classifyGrader, classifySecurity, tallyVerdicts } from '../lib/bot-verdicts.mjs';

// Real subjects from main. The review-fix definition is frozen: if one of these flips, every
// review-fix share already recorded stops being comparable with new ones.
const REVIEW_FIX = [
  'fix: harden the EU id rules found by security review',
  "fix: close the review's gaps in the erasure id listing",
  'refactor: tidy the Foundry client after simplify review',
  'fix: address run-gates correctness/grader findings on hot-reload (#705)',
  'fix: guard the memo probe against a null tool key (#651 review)',
  'fix: round-2b hardening for #601 -- root/unrestricted still too broad',
  'fix: close 5th-round gate advisories on Windows dir-name and prune-root escape',
  'fix: two more Windows-only "C:" test assumptions caught by the re-run CI',
  'fix: redesign injection-scan fix per code-review — write-time, not cooperative read-time',
  'test(review-fixes): reconcile tests with fixed behavior; suite green',
  'fix: second security-review round on #552 — fail closed at the depth boundary',
  'refactor: unify skill provenance onto ProvisionedTool (#531 /simplify)',
  'fix: correctness advisories — stale doc count, duplicate DI registration',
  'fix: take all six advisories from the CI security and correctness reviewers',
  'fix: close the inbound baggage-extraction gap CI\'s correctness/security gates found',
  'review: pair transcript writes, share the role mapper, cheapen the ownership probe',
];

const NOT_REVIEW_FIX = [
  'fix: patch frontend npm advisories and drop the withdrawn braces exception',
  'fix: clear frontend advisories and gate both ecosystems in CI',
  'chore: refresh the frontend lockfiles to clear 16 npm advisories',
  'refactor: simplify content-safety service and reuse shared test helpers',
  'fix: bind a human-gate approval to the plan and step it was queued for',
  'fix: bind review receipts to the reviewed code, not the commit SHA',
  "feat: relay a reviewer's revise instructions to the model (#321, PR3b)",
  "docs: record two recurring bug classes from this session's review cycle",
  'feat(dashboard-agent): backend AG-UI blocking-tool round-trip (PR1)',
  'ci: promote correctness-review to a required status check',
  'chore: add mechanical review gate (code-review + simplify before push)',
  'feat: add composite pre-push-review skill',
  'fix: gate the Entra-only app-token checks on an Entra issuer',
  'test: give each composition its own conversation database',
];

test('review-fix regex counts subjects that say they address review or CI-gate findings', () => {
  for (const subject of REVIEW_FIX) assert.equal(isReviewFixSubject(subject), true, subject);
});

test('review-fix regex ignores dependency advisories, product gates and work on review tooling', () => {
  for (const subject of NOT_REVIEW_FIX) assert.equal(isReviewFixSubject(subject), false, subject);
});

test('revert regex matches git and conventional reverts only', () => {
  assert.equal(isRevertSubject('Revert "feat: add the thing"'), true);
  assert.equal(isRevertSubject("revert: undo owasp-agentic-gate's build-artifact reuse"), true);
  assert.equal(isRevertSubject('revert(ci): undo the cache'), true);
  assert.equal(isRevertSubject('fix: revert a lock-scope regression in PluginRegistry'), false);
  assert.equal(isRevertSubject('Reverting is hard'), false);
});

test('finding marker parses every field and rejects unknown reviewers and tags', () => {
  const body = '<!-- rail:correctness sha=abc1234 anchor=src/A/B.cs:42 class=null-deref tag=important -->\n[Important] x';
  assert.deepEqual(parseFindingMarker(body), { reviewer: 'correctness', sha: 'abc1234', anchor: 'src/A/B.cs:42', class: 'null-deref', tag: 'important' });
  assert.equal(parseFindingMarker('<!-- rail:grader sha=abc1234 anchor=a:1 class=x tag=nit -->'), null);
  assert.equal(parseFindingMarker('<!-- rail:security sha=abc1234 anchor=a:1 class=x tag=blocker -->'), null);
  assert.equal(parseFindingMarker('[Important] no marker'), null);
});

test('severity tag is read from the visible text after the marker', () => {
  assert.equal(parseSeverityTag('<!-- rail:security sha=abc1234 anchor=a:1 class=x tag=nit -->\n[Nit] spacing'), 'nit');
  assert.equal(parseSeverityTag('[Pre-existing] old bug'), 'pre-existing');
  assert.equal(parseSeverityTag('This is [Important] mid-sentence'), null);
});

test('disposition reply parses kind and reason', () => {
  assert.deepEqual(parseDisposition('Disposition: didnt-matter — dead branch, removed in #900'), { disposition: 'didnt-matter', reason: 'dead branch, removed in #900' });
  assert.deepEqual(parseDisposition('Thanks.\nDisposition: fixed - in 1a2b3c4'), { disposition: 'fixed', reason: 'in 1a2b3c4' });
  assert.equal(parseDisposition('Disposition: ignored — nope'), null);
  assert.equal(parseDisposition('Disposition: wrong'), null);
});

test('Review-rounds line gives a number, cap, or null', () => {
  assert.equal(parseReviewRounds('## Intent\nx\n\nReview-rounds: 2\n'), 2);
  assert.equal(parseReviewRounds('Review-rounds: cap'), 'cap');
  assert.equal(parseReviewRounds('We did two review-rounds: 2 of them'), null);
  assert.equal(parseReviewRounds(null), null);
});

test('Reviewed minutes and the tier-2 sign-off parse from comment lines', () => {
  assert.equal(parseReviewedMinutes('Reviewed: 4 min — read evidence'), 4);
  assert.equal(parseReviewedMinutes('Reviewed: 2.5 min'), 2.5);
  assert.equal(parseReviewedMinutes('I Reviewed: 4 min'), null);
  assert.equal(parseSignoff('Reviewed-by: @MCKRUZ 0540d109 — read the redaction path'), '0540d109');
  assert.equal(parseSignoff('Reviewed-by: @someone 0540d109 — x'), null);
});

test('tier comes from exactly one tier label', () => {
  assert.equal(tierFromLabels(['enhancement', 'tier:1']), 'tier:1');
  assert.equal(tierFromLabels(['enhancement']), null);
  assert.equal(tierFromLabels(['tier:0', 'tier:2']), 'conflict');
  assert.equal(tierFromLabels(['tier:3']), null);
});

test('convention lines are recognised so they are not counted as review conversation', () => {
  assert.equal(isConventionComment('Disposition: fixed — x'), true);
  assert.equal(isConventionComment('Reviewed: 3 min'), true);
  assert.equal(isConventionComment('Looks fine to me'), false);
});

test('correctness verdict: all three Blocking-defects shapes, then the bottom-line token', () => {
  const head = '<!-- correctness-review -->\n## Correctness review — PR #1\n\n';
  assert.equal(classifyCorrectness(`${head}**Blocking defects:** none.\n\n**Advisory**\n- x`), 'correct');
  assert.equal(classifyCorrectness(`${head}### Blocking defects\nNone.\n\n### Advisory\n- x`), 'correct');
  assert.equal(classifyCorrectness(`${head}### Blocking defects\n\n| Anchor | Defect |\n|---|---|\n| a.cs:1 | bad |\n\n### Advisory\nNone.`), 'defects-found');
  assert.equal(classifyCorrectness(`${head}**Bottom line:** DEFECTS FOUND.`), 'defects-found');
  assert.equal(classifyCorrectness(`${head}**Bottom line:** CORRECT.`), 'correct');
  assert.equal(classifyCorrectness(`${head}Nothing structured here.`), 'unclassified');
});

test('grader and security verdicts take the first verdict token', () => {
  assert.equal(classifyGrader('## Grader verdict\n### Bottom line: **LOOKS RISKY** (moderate)'), 'looks-risky');
  assert.equal(classifyGrader('## Grader verdict\n**Bottom line: LOOKS GOOD.**'), 'looks-good');
  assert.equal(classifyGrader('## Grader verdict\nno token'), 'unclassified');
  assert.equal(classifySecurity('## Security review: PASS (no HIGH findings)'), 'pass');
  assert.equal(classifySecurity('## Security review — PR #2\n**Verdict: BLOCK**'), 'block');
});

test('only bot comments count as verdicts; a human reply quoting the reviewer does not', () => {
  const human = 'Correctness review was right: the split put `{body}` in a non-interpolated literal.';
  assert.equal(botCommentKind('MCKRUZ', '## Correctness review — PR #880\n**Blocking defects:** none.'), null);
  assert.equal(botCommentKind('github-actions', human), null);
  assert.equal(botCommentKind('github-actions', '<!-- grader-verdict -->\n## Grader verdict — PR #1'), 'grader');
  const tally = tallyVerdicts([
    { author: 'github-actions', body: '## Correctness review\n### Blocking defects\n| a | b |\n|---|---|\n| x | y |' },
    { author: 'MCKRUZ', body: human },
    { author: 'github-actions', body: '## Correctness review\n**Blocking defects:** none.' },
  ]);
  assert.deepEqual(tally.correctness, { comments: { 'defects-found': 1, correct: 1 }, final: 'correct' });
  assert.deepEqual(tally.grader, { comments: {}, final: null });
});

test('NO_DATA is the literal string the conventions report', () => {
  assert.equal(NO_DATA, 'no data');
});
