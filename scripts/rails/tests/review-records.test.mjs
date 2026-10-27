// node --test scripts/rails/tests/*.test.mjs
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { NO_DATA } from '../lib/review-parsers.mjs';
import { percentile, summarize, share, isoWeek, previousIsoWeekWindow, missingWeeks, weekStartFromLabel } from '../lib/stats.mjs';
import { parseWorkflow, compareChecks } from '../lib/checks.mjs';
import { aggregate, buildPrRecord, runsForPr } from '../lib/pr-records.mjs';
import { commitSubject, formatSummary, parseArgs, resolveWindow } from '../collect-review-metrics.mjs';

test('the commit subject is the full first line, never GitHub\'s 72-character headline', () => {
  const subject = 'fix: apply second /code-review round on #738 (vacuous proof, layering, gaps)';
  assert.equal(commitSubject(`${subject}\r\n\r\nBody line.`), subject);
  assert.equal(commitSubject('chore: one line'), 'chore: one line');
});

test('percentile interpolates between closest ranks; median is p50', () => {
  assert.equal(percentile([1, 2, 3, 4], 50), 2.5);
  assert.equal(percentile([1, 2, 3, 4, 5, 6, 7, 8, 9, 10], 90), 9.1);
  assert.equal(percentile([7], 90), 7);
  assert.equal(percentile([3, 1, 2], 0), 1);
});

test('empty sets are "no data", a real zero is a number', () => {
  assert.equal(percentile([], 50), NO_DATA);
  assert.equal(summarize([]), NO_DATA);
  assert.equal(share(0, 0), NO_DATA);
  assert.equal(share(0, 5), 0);
  assert.deepEqual(summarize([0, 0]), { n: 2, mean: 0, median: 0, p90: 0, max: 0 });
});

test('ISO weeks, including the year boundary', () => {
  assert.equal(isoWeek(new Date('2026-10-27T12:00:00Z')), '2026-W44');
  assert.equal(isoWeek(new Date('2027-01-01T00:00:00Z')), '2026-W53');
  assert.equal(isoWeek(new Date('2024-12-30T00:00:00Z')), '2025-W01');
  assert.equal(weekStartFromLabel('2026-W44').toISOString(), '2026-10-26T00:00:00.000Z');
});

test('the scheduled window is the last complete Monday-to-Monday week', () => {
  const window = previousIsoWeekWindow(new Date('2026-11-02T05:17:00Z'));
  assert.equal(window.since.toISOString(), '2026-10-26T00:00:00.000Z');
  assert.equal(window.until.toISOString(), '2026-11-02T00:00:00.000Z');
  assert.equal(window.label, '2026-W44');
});

test('a missing week between the last file and this run is reported', () => {
  assert.deepEqual(missingWeeks(['2026-W43', '2026-W45'], '2026-W47'), ['2026-W44', '2026-W46']);
  assert.deepEqual(missingWeeks(['2026-W46'], '2026-W47'), []);
  assert.deepEqual(missingWeeks([], '2026-W47'), []);
  assert.deepEqual(missingWeeks(['2026-W52', '2026-W53'], '2027-W02'), ['2027-W01']);
});

test('explicit windows are UTC midnights with until exclusive', () => {
  const options = parseArgs(['--since', '2026-09-27', '--until', '2026-10-27', '--out', 'x.json']);
  assert.equal(options.out, 'x.json');
  const window = resolveWindow(options);
  assert.equal((window.until - window.since) / 86400000, 30);
  assert.throws(() => resolveWindow(parseArgs(['--since', '2026-10-27', '--until', '2026-10-01'])));
});

const CI_YML = `name: CI
on:
  push:
  pull_request:
jobs:
  build-and-test:
    runs-on: ubuntu-latest
    steps:
      - name: Build
        run: dotnet build
  frontend-tests:
    name: frontend (\${{ matrix.project }})
    strategy:
      matrix:
        project: [ Presentation.Dashboard, Presentation.WebUI ]
    steps:
      - run: npm test
  tier:
    name: tier-gate job
    steps:
      - run: gh api repos/x/y/statuses/abc -f state=success -f context=tier-gate
`;

test('workflow parsing yields the check names a PR would report', () => {
  const workflow = parseWorkflow(CI_YML);
  assert.deepEqual(workflow.triggers.sort(), ['pull_request', 'push']);
  assert.deepEqual(workflow.contexts, ['build-and-test', 'frontend (Presentation.Dashboard)', 'frontend (Presentation.WebUI)', 'tier-gate job']);
  assert.deepEqual(workflow.statusContexts, ['tier-gate']);
  assert.deepEqual(parseWorkflow('on: [push, workflow_dispatch]\njobs:\n  a:\n    runs-on: x\n').triggers, ['push', 'workflow_dispatch']);
});

test('a required check that no workflow declares is reported (the plan\'s planted-check proof)', () => {
  const result = compareChecks({
    requiredLive: ['build-and-test', 'planted-check'],
    requiredCommitted: ['build-and-test', 'planted-in-json'],
    declaredOnPullRequest: ['build-and-test', 'grader'],
    observedOnPullRequests: ['build-and-test', 'grader'],
  });
  assert.deepEqual(result.committed_not_declared, ['planted-in-json']);
  assert.deepEqual(result.committed_not_live, ['planted-in-json']);
  assert.deepEqual(result.required_not_declared, ['planted-check']);
  assert.deepEqual(result.required_not_observed, ['planted-check']);
  assert.deepEqual(result.live_not_committed, ['planted-check']);
  assert.deepEqual(result.observed_not_required, ['grader']);
  assert.equal(compareChecks({ requiredLive: ['a'], requiredCommitted: [], declaredOnPullRequest: [], observedOnPullRequests: [] }).required_not_observed, NO_DATA);
});

const basePr = (overrides = {}) => ({
  number: 1, createdAt: '2026-10-01T10:00:00Z', mergedAt: '2026-10-01T12:00:00Z', headRefName: 'fix/x',
  additions: 10, deletions: 5, changedFiles: 2, body: '## Intent\nx', labels: [], commitsTotal: 3,
  commits: [
    { subject: 'feat: add the thing', parents: 1 },
    { subject: 'fix: address code-review findings on the thing', parents: 1 },
    { subject: "Merge branch 'main' into fix/x", parents: 2 },
  ],
  comments: [], reviews: [], threads: [],
  ...overrides,
});

const run = (overrides) => ({ head_branch: 'fix/x', event: 'pull_request', created_at: '2026-10-01T11:00:00Z', conclusion: 'success', run_attempt: 1, ...overrides });

test('runs are matched to a PR by branch, event and its open-to-merge interval', () => {
  const runs = [run({}), run({ head_branch: 'other' }), run({ event: 'push' }), run({ created_at: '2026-09-30T00:00:00Z' }), run({ created_at: '2026-10-01T12:00:00Z' })];
  assert.equal(runsForPr(runs, basePr()).length, 2);
});

test('a PR with no conventions in use reports "no data" for every convention field', () => {
  const record = buildPrRecord(basePr(), { 'correctness-review': [run({}), run({ conclusion: 'cancelled' })] });
  assert.equal(record.commits, 2);
  assert.equal(record.review_fix_commits, 1);
  assert.equal(record.open_to_merge_hours, 2);
  assert.deepEqual(record.ci['correctness-review'], { runs: 2, verdict_runs: 1, failed: 0, cancelled: 1, attempts: 2 });
  assert.equal(record.reviewed_minutes, NO_DATA);
  assert.equal(record.review_rounds, NO_DATA);
  assert.equal(record.signoff_sha, NO_DATA);
  const totals = aggregate([record], 7);
  assert.equal(totals.matt_minutes_by_tier, NO_DATA);
  assert.equal(totals.rounds_to_pass.local, NO_DATA);
  assert.equal(totals.findings.noise_rate, NO_DATA);
  assert.equal(totals.tier_distribution, NO_DATA);
  assert.equal(totals.review_fix.share, 0.5);
  assert.deepEqual(totals.rounds_to_pass.ci_correctness_runs, { n: 1, mean: 2, median: 2, p90: 2, max: 2 });
});

test('conventions, once used, fill the fields: minutes by tier, rounds, findings and noise rate', () => {
  const marker = (tag) => `<!-- rail:correctness sha=abc1234 anchor=a.cs:1 class=x tag=${tag} -->\n[${tag}] text`;
  const thread = (tag, disposition) => ({
    isResolved: true,
    comments: [{ author: 'github-actions', body: marker(tag) }, ...(disposition ? [{ author: 'MCKRUZ', body: `Disposition: ${disposition} — why` }] : [])],
  });
  const pr = basePr({
    labels: ['tier:1'],
    body: '## Intent\nx\nReview-rounds: 2',
    comments: [{ author: 'MCKRUZ', body: 'Reviewed: 6 min — read the evidence' }, { author: 'MCKRUZ', body: 'Can you rename this?' }],
    threads: [thread('important', 'fixed'), thread('nit', 'didnt-matter'), thread('nit', 'wrong'), thread('nit', null)],
  });
  const record = buildPrRecord(pr, {});
  assert.equal(record.tier, 'tier:1');
  assert.equal(record.reviewed_minutes, 6);
  assert.equal(record.review_rounds, 2);
  assert.equal(record.matt_comments, 1);
  assert.deepEqual(record.findings_by_disposition, { fixed: 1, 'didnt-matter': 1, wrong: 1, undispositioned: 1 });
  const totals = aggregate([record, buildPrRecord(basePr({ number: 2, body: 'Review-rounds: cap' }), {})], 7);
  assert.deepEqual(totals.matt_minutes_by_tier, { 'tier:1': { n: 1, mean: 6, median: 6, p90: 6, max: 6, total: 6 } });
  assert.deepEqual(totals.rounds_to_pass.local, { prs_reporting: 2, cap_hit: 1, rounds: { n: 1, mean: 2, median: 2, p90: 2, max: 2 } });
  assert.equal(totals.findings.noise_rate, 0.6667);
  assert.deepEqual(totals.findings.by_tag, { important: 1, nit: 3 });
  assert.deepEqual(totals.tier_distribution, { 'tier:1': 1, unlabelled: 1 });
});

test('the job summary shows "no data" for unused conventions and names a missing week', () => {
  const totals = aggregate([buildPrRecord(basePr(), {})], 7);
  const report = {
    window: { label: '2026-W44', since: 's', until: 'u', days: 7 },
    aggregates: totals,
    guardrails: { reverts: { count: 0 }, incident_issues_opened: NO_DATA },
    gates: { checks: { required_not_declared: ['planted-check'], required_not_observed: [] } },
    telemetry_health: { weekly_files_seen: 2, missing_weeks_before_this_one: ['2026-W43'] },
    warnings: [],
  };
  const summary = formatSummary(report);
  assert.match(summary, /\| Noise rate \| no data \|/);
  assert.match(summary, /\| Missing earlier weeks \| \["2026-W43"\] \|/);
  assert.match(summary, /\| Required checks nothing declares \| \["planted-check"\] \|/);
});
