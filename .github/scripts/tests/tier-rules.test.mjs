// Tests for tier-rules.mjs. The dangerous direction is a change classed too LOW, so most cases
// prove that something which could weaken a guard is never tier 0 or tier 1.
// Run: node --test ".github/scripts/tests/*.test.mjs"
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { classify, parseDiff, isGated, SIZE_LIMIT } from '../tier-rules.mjs';

const file = (path, over = {}) => ({ path, status: 'M', added: 3, deleted: 1, ...over });
const tierOf = (files, opts) => classify(files, opts).tier;
const added = (map) => ({ addedText: (p) => map[p] ?? '' });

test('plain documentation is tier 0', () => {
  assert.equal(tierOf([file('documentation/onboarding/chapter-03.md'), file('README.md'), file('documentation/architecture/index.html')]), 0);
});

test('a brand-new test file is tier 0', () => {
  assert.equal(tierOf([file('src/Content/Tests/App.Tests/WidgetTests.cs', { status: 'A', deleted: 0, added: 40 })]), 0);
});

test('ANY edit to an existing test is tier 1 — inserting `return;` disables a test without deleting a line', () => {
  assert.equal(tierOf([file('src/Content/Tests/App.Tests/WidgetTests.cs', { deleted: 0, added: 1 })]), 1);
  assert.equal(tierOf([file('src/Content/Tests/App.Tests/WidgetTests.cs', { status: 'D', added: 0, deleted: 30 })]), 1);
});

test('a new test file that adds a module initializer or assembly attribute is not tier 0', () => {
  const p = 'src/Content/Tests/App.Tests/Init.cs';
  assert.equal(tierOf([file(p, { status: 'A', deleted: 0 })], added({ [p]: '+[ModuleInitializer]\n+internal static void Init() {}' })), 1);
  assert.equal(tierOf([file(p, { status: 'A', deleted: 0 })], added({ [p]: '+[assembly: CollectionBehavior(DisableTestParallelization = true)]' })), 1);
});

test('a test project file is not a test: package changes are tier 1', () => {
  assert.equal(tierOf([file('src/Content/Tests/App.Tests/App.Tests.csproj', { status: 'A', deleted: 0 })]), 1);
});

test('agent instruction files and the review loop are never tier 0', () => {
  for (const p of ['CLAUDE.md', 'AGENTS.md', 'src/Content/CLAUDE.md', 'documentation/agents.md', '.claude/rules/review-cadence.md', '.claude/skills/pre-push-review/SKILL.md', 'skills/research/SKILL.md', 'agents/planner/AGENT.md']) {
    assert.ok(tierOf([file(p)]) >= 1, p);
  }
});

test('docs-site scripts, styles and data are not plain docs', () => {
  for (const p of ['documentation/onboarding/assets/js/main.js', 'documentation/onboarding/assets/css/site.css', 'documentation/build.sh', 'documentation/reference/data.json', 'documentation/x/diagram.svg']) {
    assert.equal(tierOf([file(p)]), 1, p);
  }
});

test('docs-site HTML that adds something executable is not plain docs', () => {
  const p = 'documentation/onboarding/chapter-01.html';
  assert.equal(tierOf([file(p)], added({ [p]: '+<p>text</p>' })), 0);
  for (const bad of ['+<script src="x.js"></script>', '+<a href="javascript:alert(1)">x</a>', '+<img src=x onerror="steal()">']) {
    assert.equal(tierOf([file(p)], added({ [p]: bad })), 1, bad);
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

test("the repo's other auth folder spellings are gated too", () => {
  for (const p of ['src/Content/Infrastructure/Infrastructure.AI.MCPServer/Authentication/JwtSetup.cs', 'src/Content/Infrastructure/Infrastructure.AI.MCPServer/Authorization/Policy.cs', 'src/Content/Presentation/Presentation.Dashboard/src/auth/token.ts']) {
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

test('git diff -z output parses, including binary files and adds', () => {
  const files = parseDiff('M\0src/A.cs\0A\0docs/x.png\0D\0README.md\0', '3\t1\tsrc/A.cs\0-\t-\tdocs/x.png\0' + '0\t9\tREADME.md\0');
  assert.deepEqual(files, [
    { path: 'src/A.cs', status: 'M', added: 3, deleted: 1 },
    { path: 'docs/x.png', status: 'A', added: 0, deleted: 0 },
    { path: 'README.md', status: 'D', added: 0, deleted: 9 },
  ]);
});

test('a path with non-ASCII characters is still gated (no C-quoting under -z)', () => {
  const files = parseDiff('M\0src/Content/Auth/Ünïcode.cs\0', '2\t0\tsrc/Content/Auth/Ünïcode.cs\0');
  assert.equal(classify(files).tier, 2);
});

test('a fully qualified module initializer in a new test file is caught too', () => {
  const p = 'src/Content/Tests/App.Tests/Init.cs';
  assert.equal(tierOf([file(p, { status: 'A', deleted: 0 })], added({ [p]: '+[System.Runtime.CompilerServices.ModuleInitializer]' })), 1);
});
