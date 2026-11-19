#!/usr/bin/env node
// Collects review-process telemetry for PRs merged in a window and writes one JSON file.
//
//   node scripts/rails/collect-review-metrics.mjs --since 2026-09-27 --until 2026-10-27 --out out.json
//   node scripts/rails/collect-review-metrics.mjs --previous-week --out-dir data/   (writes <ISO-week>.json)
//
// Options: --repo owner/name (default $GITHUB_REPOSITORY, else `gh repo view`), --ref <git ref of main>
// (default origin/main), --data-dir <dir> of earlier weekly files (reports missing weeks),
// --summary <file> (appends a Markdown digest, e.g. $GITHUB_STEP_SUMMARY).
// Needs `gh` (authenticated) and `git` with main's full history. Any failed call fails the run:
// a missing number must never be confused with "no data". The field meanings are in
// .github/rails-telemetry/README.md; the definitions are printed into every output file.

import { execFileSync } from 'node:child_process';
import { appendFileSync, existsSync, mkdirSync, readdirSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { INCIDENT_LABEL, NO_DATA, WEEKLY_FILE, commitSubject, isRevertSubject } from './lib/review-parsers.mjs';
import { REVIEWER_WORKFLOWS, aggregate, buildPrRecord } from './lib/pr-records.mjs';
import { compareChecks, parseWorkflow } from './lib/checks.mjs';
import { buildDefinitions } from './lib/definitions.mjs';
import { isoWeek, missingWeeks, previousIsoWeekWindow } from './lib/stats.mjs';

const DAY = 86400000;
const MAX_BUFFER = 512 * 1024 * 1024;
const PR_PAGE_SIZE = 20;
// The search API and filtered workflow-run listings stop at 1,000 results; reaching that means data is missing.
const API_RESULT_CAP = 1000;
const ISSUE_LIMIT = 500;

const run = (command, args) => execFileSync(command, args, { encoding: 'utf8', maxBuffer: MAX_BUFFER, stdio: ['ignore', 'pipe', 'pipe'] });
const gh = (args) => run('gh', args);
const ghJson = (args) => JSON.parse(gh(args));
const git = (args) => run('git', args).trim();
const jsonLines = (text) => text.split('\n').filter((line) => line.trim() !== '').map((line) => JSON.parse(line));

export function parseArgs(argv) {
  const options = { ref: 'origin/main' };
  for (let i = 0; i < argv.length; i += 1) {
    const flag = argv[i];
    if (flag === '--previous-week') { options.previousWeek = true; continue; }
    const value = argv[i + 1];
    if (!flag.startsWith('--') || value === undefined) throw new Error(`bad argument: ${flag}`);
    options[flag.slice(2).replace(/-([a-z])/g, (_, letter) => letter.toUpperCase())] = value;
    i += 1;
  }
  return options;
}

export function resolveWindow(options, now = new Date()) {
  if (options.previousWeek) return previousIsoWeekWindow(now);
  if (!options.since || !options.until) throw new Error('need --since and --until, or --previous-week');
  const since = new Date(`${options.since}T00:00:00Z`);
  const until = new Date(`${options.until}T00:00:00Z`);
  if (!(since < until)) throw new Error('--since must be before --until');
  return { since, until, label: options.label ?? `${options.since}_${options.until}` };
}

const PR_QUERY = `query($q: String!, $after: String) {
  search(query: $q, type: ISSUE, first: ${PR_PAGE_SIZE}, after: $after) {
    issueCount pageInfo { hasNextPage endCursor }
    nodes { ... on PullRequest {
      number createdAt mergedAt headRefName additions deletions changedFiles body
      labels(first: 20) { nodes { name } }
      commits(first: 100) { totalCount nodes { commit { message parents { totalCount } } } }
      comments(first: 100) { totalCount nodes { author { login } body } }
      reviews(first: 50) { nodes { author { login } body } }
      reviewThreads(first: 50) { totalCount nodes { isResolved comments(first: 20) { nodes { author { login } body } } } }
      head: commits(last: 1) { nodes { commit { statusCheckRollup { contexts(first: 60) { nodes {
        __typename ... on CheckRun { name } ... on StatusContext { context } } } } } } }
    } }
  }
}`;

const login = (node) => node.author?.login ?? 'ghost';

function normalizePr(node, warnings) {
  if (node.comments.totalCount > node.comments.nodes.length) warnings.push(`PR #${node.number}: ${node.comments.totalCount} comments, first 100 counted`);
  if (node.reviewThreads.totalCount > node.reviewThreads.nodes.length) warnings.push(`PR #${node.number}: ${node.reviewThreads.totalCount} review threads, first 50 counted`);
  const rollup = node.head.nodes[0]?.commit.statusCheckRollup?.contexts.nodes ?? [];
  return {
    number: node.number, createdAt: node.createdAt, mergedAt: node.mergedAt, headRefName: node.headRefName,
    additions: node.additions, deletions: node.deletions, changedFiles: node.changedFiles, body: node.body ?? '',
    labels: node.labels.nodes.map((label) => label.name),
    commitsTotal: node.commits.totalCount,
    commits: node.commits.nodes.map(({ commit }) => ({ subject: commitSubject(commit.message), parents: commit.parents.totalCount })),
    comments: node.comments.nodes.map((comment) => ({ author: login(comment), body: comment.body })),
    reviews: node.reviews.nodes.map((review) => ({ author: login(review), body: review.body ?? '' })),
    threads: node.reviewThreads.nodes.map((thread) => ({
      isResolved: thread.isResolved,
      comments: thread.comments.nodes.map((comment) => ({ author: login(comment), body: comment.body })),
    })),
    observedContexts: rollup.map((context) => context.name ?? context.context).filter(Boolean),
  };
}

function fetchMergedPrs(repo, window, warnings) {
  const query = `repo:${repo} is:pr is:merged merged:${window.since.toISOString()}..${window.until.toISOString()}`;
  const prs = [];
  let after = null;
  do {
    const args = ['api', 'graphql', '-f', `query=${PR_QUERY}`, '-f', `q=${query}`];
    if (after) args.push('-f', `after=${after}`);
    const { data } = ghJson(args);
    if (data.search.issueCount >= API_RESULT_CAP) throw new Error(`${data.search.issueCount} PRs match; search returns at most ${API_RESULT_CAP}. Use a shorter window.`);
    prs.push(...data.search.nodes.map((node) => normalizePr(node, warnings)));
    after = data.search.pageInfo.hasNextPage ? data.search.pageInfo.endCursor : null;
  } while (after);
  const inWindow = (pr) => Date.parse(pr.mergedAt) >= window.since.getTime() && Date.parse(pr.mergedAt) < window.until.getTime();
  return prs.filter(inWindow).sort((a, b) => a.number - b.number);
}

function fetchRuns(repo, workflow, createdSince) {
  const runs = jsonLines(gh(['api', '-X', 'GET', `repos/${repo}/actions/workflows/${workflow}.yml/runs`,
    '-f', 'event=pull_request', '-f', `created=>=${createdSince.toISOString()}`, '-f', 'per_page=100', '--paginate',
    '--jq', '.workflow_runs[] | {head_branch, event, created_at, conclusion, run_attempt}']));
  if (runs.length >= API_RESULT_CAP) throw new Error(`${workflow}: ${runs.length} runs listed; the runs API stops at ${API_RESULT_CAP}. Use a shorter window.`);
  return runs;
}

function windowEndCommit(ref, until) {
  const sha = git(['rev-list', '--first-parent', '-1', `--before=${until.toISOString()}`, ref]);
  if (!sha) throw new Error(`no commit on ${ref} before ${until.toISOString()}`);
  return sha;
}

const filesAt = (sha, dir) => git(['ls-tree', '--full-tree', '--name-only', `${sha}`, `${dir}/`]).split('\n').filter(Boolean);
const showAt = (sha, path) => git(['show', `${sha}:${path}`]);

function declaredContexts(sha) {
  const declared = new Set();
  for (const path of filesAt(sha, '.github/workflows').filter((file) => /\.ya?ml$/.test(file))) {
    const workflow = parseWorkflow(showAt(sha, path));
    if (workflow.triggers.some((event) => event.startsWith('pull_request'))) workflow.contexts.forEach((name) => declared.add(name));
    workflow.statusContexts.forEach((name) => declared.add(name));
  }
  return [...declared];
}

function committedRequiredContexts(sha) {
  const required = new Set();
  for (const path of filesAt(sha, '.github/rulesets').filter((file) => file.endsWith('.json'))) {
    const ruleset = JSON.parse(showAt(sha, path));
    const include = ruleset.conditions?.ref_name?.include ?? [];
    if (!include.some((ref) => ref === '~DEFAULT_BRANCH' || ref === 'refs/heads/main')) continue;
    for (const rule of ruleset.rules ?? []) (rule.parameters?.required_status_checks ?? []).forEach((check) => required.add(check.context));
  }
  return [...required];
}

function liveRequiredContexts(repo) {
  const rules = ghJson(['api', `repos/${repo}/rules/branches/main`]);
  return rules.flatMap((rule) => (rule.parameters?.required_status_checks ?? []).map((check) => check.context));
}

function issuesWithLabel(repo, label, state) {
  const issues = ghJson(['issue', 'list', '--repo', repo, '--label', label, '--state', state, '--limit', String(ISSUE_LIMIT), '--json', 'number,title,createdAt,state']);
  if (issues.length >= ISSUE_LIMIT) throw new Error(`label ${label}: ${issues.length} issues listed, the limit asked for. Raise ISSUE_LIMIT.`);
  return issues;
}

function guardrails(repo, ref, window, labels) {
  const log = git(['log', ref, `--since=${window.since.toISOString()}`, `--until=${window.until.toISOString()}`, '--format=%H%x1f%s']);
  const reverts = log.split('\n').filter(Boolean).map((line) => line.split('\x1f')).filter(([, subject]) => isRevertSubject(subject))
    .map(([sha, subject]) => ({ sha: sha.slice(0, 12), subject }));
  const incidentLabels = labels.filter((name) => INCIDENT_LABEL.test(name));
  const inWindow = (issue) => Date.parse(issue.createdAt) >= window.since.getTime() && Date.parse(issue.createdAt) < window.until.getTime();
  const incidents = incidentLabels.length === 0 ? NO_DATA
    : incidentLabels.flatMap((label) => issuesWithLabel(repo, label, 'all')).filter(inWindow).map(({ number, title }) => ({ number, title }));
  const breakers = labels.includes('rails:breaker') ? issuesWithLabel(repo, 'rails:breaker', 'open').map(({ number, title }) => ({ number, title })) : NO_DATA;
  const days = (window.until - window.since) / DAY;
  return {
    reverts: { count: reverts.length, per_week: Math.round((reverts.length * 7 * 100) / days) / 100, commits: reverts },
    incident_issues_opened: incidents === NO_DATA ? NO_DATA : { count: incidents.length, issues: incidents },
    incident_labels: incidentLabels.length ? incidentLabels : NO_DATA,
    open_breakers: breakers === NO_DATA ? NO_DATA : { count: breakers.length, issues: breakers },
  };
}

const CONVENTION_LABELS = ['tier:0', 'tier:1', 'tier:2', 'rails:breaker', 'incident', 'risk:high', 'accepted-risk:correctness', 'accepted-risk:security'];

function telemetryHealth(dataDir, label) {
  if (!dataDir) return NO_DATA;
  if (!existsSync(dataDir)) return { weekly_files_seen: 0, missing_weeks_before_this_one: [] };
  const existing = readdirSync(dataDir).map((file) => WEEKLY_FILE.exec(file)?.[1]).filter(Boolean);
  return { weekly_files_seen: existing.length, missing_weeks_before_this_one: missingWeeks(existing, label) };
}

export function collect(options, now = new Date()) {
  const repo = options.repo ?? process.env.GITHUB_REPOSITORY ?? gh(['repo', 'view', '--json', 'nameWithOwner', '--jq', '.nameWithOwner']).trim();
  const window = resolveWindow(options, now);
  const warnings = [];
  const prs = fetchMergedPrs(repo, window, warnings);
  const earliestOpen = prs.reduce((earliest, pr) => Math.min(earliest, Date.parse(pr.createdAt)), window.since.getTime());
  const runsByWorkflow = Object.fromEntries(REVIEWER_WORKFLOWS.map((workflow) => [workflow, fetchRuns(repo, workflow, new Date(earliestOpen))]));
  const records = prs.map((pr) => buildPrRecord(pr, runsByWorkflow));
  const days = (window.until - window.since) / DAY;
  const sha = windowEndCommit(options.ref, window.until);
  const labels = ghJson(['label', 'list', '--repo', repo, '--limit', '500', '--json', 'name']).map((label) => label.name);
  return {
    schema_version: 1,
    generated_at: now.toISOString(),
    repo,
    window: { label: window.label, since: window.since.toISOString(), until: window.until.toISOString(), until_exclusive: true, days, iso_week_of_since: isoWeek(window.since) },
    instrument: { script: 'scripts/rails/collect-review-metrics.mjs', git_ref: options.ref, window_end_commit: sha },
    definitions: buildDefinitions(),
    aggregates: aggregate(records, days),
    guardrails: guardrails(repo, options.ref, window, labels),
    gates: {
      checks: compareChecks({
        requiredLive: liveRequiredContexts(repo),
        requiredCommitted: committedRequiredContexts(sha),
        declaredOnPullRequest: declaredContexts(sha),
        observedOnPullRequests: [...new Set(prs.flatMap((pr) => pr.observedContexts))],
      }),
      convention_labels: Object.fromEntries(CONVENTION_LABELS.map((name) => [name, labels.includes(name)])),
      claude_md_bytes: Number(git(['cat-file', '-s', `${sha}:CLAUDE.md`])),
    },
    telemetry_health: telemetryHealth(options.dataDir, window.label),
    warnings,
    prs: records,
  };
}

const cell = (value) => (typeof value === 'object' && value !== null ? JSON.stringify(value) : String(value));

/** A short Markdown digest of a report, for the Actions job summary. */
export function formatSummary(report) {
  const a = report.aggregates;
  const checks = report.gates.checks;
  const health = report.telemetry_health;
  const rows = [
    ['PRs merged', `${a.pr_count} (${a.prs_per_week}/week)`],
    ['Review-fix commit share', `${cell(a.review_fix.share)} (${a.review_fix.commits}/${a.review_fix.non_merge_commits})`],
    ['PRs with ≥3 review-fix commits', cell(a.review_fix.share_of_prs_with_at_least[3])],
    ['CI correctness runs per PR', cell(a.rounds_to_pass.ci_correctness_runs)],
    ['Local review rounds', cell(a.rounds_to_pass.local)],
    ['Noise rate', cell(a.findings.noise_rate)],
    ["Matt's minutes by tier", cell(a.matt_minutes_by_tier)],
    ['Reverts', cell(report.guardrails.reverts.count)],
    ['Incident issues opened', cell(report.guardrails.incident_issues_opened === NO_DATA ? NO_DATA : report.guardrails.incident_issues_opened.count)],
    ['Required checks nothing declares', cell(checks.required_not_declared)],
    ['Required checks not seen on PRs', cell(checks.required_not_observed)],
    ['Missing earlier weeks', cell(health === NO_DATA ? NO_DATA : health.missing_weeks_before_this_one)],
  ];
  return [
    `### Rails telemetry ${report.window.label}`,
    '',
    `Window ${report.window.since} → ${report.window.until} (exclusive), ${report.window.days} days.`,
    '',
    '| Measure | Value |',
    '| --- | --- |',
    ...rows.map(([name, value]) => `| ${name} | ${value.replace(/\|/g, '\\|')} |`),
    '',
    ...(report.warnings.length ? ['Warnings:', ...report.warnings.map((warning) => `- ${warning}`), ''] : []),
  ].join('\n');
}

function main() {
  const options = parseArgs(process.argv.slice(2));
  const report = collect(options);
  const out = options.out ?? (options.outDir ? join(options.outDir, `${report.window.label}.json`) : null);
  if (!out) throw new Error('need --out <file> or --out-dir <dir>');
  mkdirSync(dirname(out), { recursive: true });
  writeFileSync(out, `${JSON.stringify(report, null, 2)}\n`);
  if (options.summary) appendFileSync(options.summary, `${formatSummary(report)}\n`);
  const a = report.aggregates;
  process.stdout.write(`${out}: ${a.pr_count} PRs, review-fix share ${a.review_fix.share}, ${report.guardrails.reverts.count} reverts\n`);
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    main();
  } catch (error) {
    process.stderr.write(`collect-review-metrics: ${error.stderr ?? ''}${error.message}\n`);
    process.exit(1);
  }
}
