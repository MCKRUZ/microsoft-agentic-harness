// Tests for tier-rules.mjs. The dangerous direction is a change classed too LOW, so most cases
// prove that something which could weaken a guard is never tier 0 or tier 1.
// Run: node --test ".github/scripts/tests/*.test.mjs"
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { classify, parseDiff, isGated, SIZE_LIMIT } from '../tier-rules.mjs';

const file = (path, over = {}) => ({ path, status: 'M', added: 3, deleted: 1, ...over });
const tierOf = (files, opts) => classify(files, opts).tier;

test('plain documentation is tier 0', () => {
  assert.equal(tierOf([file('documentation/onboarding/chapter-03.md'), file('README.md')]), 0);
});

test('a test file that only adds lines is tier 0', () => {
  assert.equal(tierOf([file('src/Content/Tests/App.Tests/WidgetTests.cs', { status: 'A', deleted: 0, added: 40 })]), 0);
});

test('editing or deleting an existing test is NOT tier 0 — that is how a guard gets weakened', () => {
  assert.equal(tierOf([file('src/Content/Tests/App.Tests/WidgetTests.cs', { deleted: 2 })]), 1);
  assert.equal(tierOf([file('src/Content/Tests/App.Tests/WidgetTests.cs', { status: 'D', added: 0, deleted: 30 })]), 1);
});

test('a test project file is not a test: package changes are tier 1', () => {
  assert.equal(tierOf([file('src/Content/Tests/App.Tests/App.Tests.csproj', { deleted: 0 })]), 1);
});

test('the review loop and its instructions are never tier 0', () => {
  for (const p of ['CLAUDE.md', 'src/Content/CLAUDE.md', '.claude/rules/review-cadence.md', '.claude/skills/pre-push-review/SKILL.md', 'skills/research/SKILL.md', 'agents/planner/AGENT.md']) {
    assert.ok(tierOf([file(p)]) >= 1, p);
  }
});

test('the published OpenAPI contract is not plain docs', () => {
  assert.equal(tierOf([file('documentation/onboarding/assets/openapi/bundle-api.yaml')]), 1);
});

test('ordinary source is tier 1', () => {
  assert.equal(tierOf([file('src/Content/Application/Widget.cs')]), 1);
});

test('the factory and the locks are tier 2', () => {
  for (const p of ['.github/workflows/ci.yml', '.github/correctness-review-rubric.md', '.claude/hooks/review-gate.ps1', '.claude/settings.json', 'scripts/rails/run-gates.sh', 'infra/main.bicep', 'src/Content/Infrastructure/Auth/JwtOptions.cs', 'src/Content/Domain/Identity/User.cs', 'src/X/Migrations/0001_init.cs']) {
    assert.equal(tierOf([file(p)]), 2, p);
  }
});

test('a gated segment must be a directory, not a file-name fragment', () => {
  assert.equal(isGated('src/Content/Domain/AuthorName.cs'), false);
  assert.equal(isGated('src/Content/Auth.cs'), false);
});

test('the security gate wanting a review raises the tier to 2', () => {
  assert.equal(tierOf([file('src/Content/Application/Runs/RunHandler.cs')], { securityRequired: true }), 2);
});

test(`more than ${SIZE_LIMIT} changed lines under src/ is tier 2; exactly the limit is not`, () => {
  assert.equal(tierOf([file('src/A.cs', { added: 600, deleted: 401 })]), 2);
  assert.equal(tierOf([file('src/A.cs', { added: 600, deleted: 400 })]), 1);
});

test('large documentation changes do not count toward the size limit', () => {
  assert.equal(tierOf([file('documentation/guide.md', { added: 5000, deleted: 0 })]), 0);
});

test('one higher-tier file lifts the whole change', () => {
  assert.equal(tierOf([file('README.md'), file('.github/workflows/ci.yml')]), 2);
  assert.equal(tierOf([file('README.md'), file('src/A.cs')]), 1);
});

test('reasons say why, for the PR comment', () => {
  const { reasons } = classify([file('.github/workflows/ci.yml')]);
  assert.match(reasons[0], /gated path: \.github\/workflows\/ci\.yml/);
});

test('git diff output parses, including binary files and adds', () => {
  const files = parseDiff('M\tsrc/A.cs\nA\tdocs/x.png\nD\tREADME.md\n', '3\t1\tsrc/A.cs\n-\t-\tdocs/x.png\n0\t9\tREADME.md\n');
  assert.deepEqual(files, [
    { path: 'src/A.cs', status: 'M', added: 3, deleted: 1 },
    { path: 'docs/x.png', status: 'A', added: 0, deleted: 0 },
    { path: 'README.md', status: 'D', added: 0, deleted: 9 },
  ]);
});
