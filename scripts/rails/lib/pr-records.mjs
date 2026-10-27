// Turns one merged PR (as fetched) into a flat record, then folds the records into window aggregates.
// Pure: the collector does the fetching and hands plain objects in.

import {
  NO_DATA, isReviewFixSubject, isConventionComment, parseDisposition, parseFindingMarker,
  parseReviewRounds, parseReviewedMinutes, parseSignoff, tierFromLabels,
} from './review-parsers.mjs';
import { tallyVerdicts } from './bot-verdicts.mjs';
import { countBy, share, summarize } from './stats.mjs';

export const REVIEWER_WORKFLOWS = Object.freeze(['correctness-review', 'security-review', 'grader']);
export const HUMAN_REVIEWER = 'MCKRUZ';
const HOURS = 3600000;

/** Runs of one workflow that belong to this PR: same head branch, pull_request event, opened→merged. */
export function runsForPr(runs, pr) {
  const opened = Date.parse(pr.createdAt);
  const merged = Date.parse(pr.mergedAt);
  return runs.filter((run) => {
    const created = Date.parse(run.created_at);
    return run.event === 'pull_request' && run.head_branch === pr.headRefName && created >= opened && created <= merged;
  });
}

/** Per-workflow run counts. A "verdict run" finished success or failure (not cancelled/skipped). */
export function summarizeRuns(runs) {
  const conclusion = (run) => run.conclusion ?? 'in-progress';
  return {
    runs: runs.length,
    verdict_runs: runs.filter((run) => ['success', 'failure'].includes(conclusion(run))).length,
    failed: runs.filter((run) => conclusion(run) === 'failure').length,
    cancelled: runs.filter((run) => conclusion(run) === 'cancelled').length,
    attempts: runs.reduce((sum, run) => sum + (run.run_attempt ?? 1), 0),
  };
}

/** Inline findings: the first comment of a review thread carries the marker; any reply may dispose it. */
export function findingsFromThreads(threads) {
  const findings = [];
  for (const thread of threads) {
    const [first, ...replies] = thread.comments;
    const marker = first && parseFindingMarker(first.body);
    if (!marker) continue;
    const disposed = replies.map((reply) => parseDisposition(reply.body)).filter(Boolean).at(-1);
    findings.push({ ...marker, disposition: disposed?.disposition ?? 'undispositioned', resolved: thread.isResolved });
  }
  return findings;
}

export function buildPrRecord(pr, runsByWorkflow) {
  const nonMerge = pr.commits.filter((commit) => commit.parents < 2);
  const reviewFix = nonMerge.filter((commit) => isReviewFixSubject(commit.subject));
  const human = (comment) => comment.author === HUMAN_REVIEWER;
  const mattIssueComments = pr.comments.filter(human);
  const mattAll = [...mattIssueComments, ...pr.reviews.filter((review) => human(review) && review.body.trim() !== ''),
    ...pr.threads.flatMap((thread) => thread.comments).filter(human)];
  const findings = findingsFromThreads(pr.threads);
  const minutes = mattIssueComments.map((comment) => parseReviewedMinutes(comment.body)).filter((value) => value !== null);
  const signoffs = mattIssueComments.map((comment) => parseSignoff(comment.body)).filter(Boolean);
  const ci = {};
  for (const workflow of REVIEWER_WORKFLOWS) ci[workflow] = summarizeRuns(runsForPr(runsByWorkflow[workflow] ?? [], pr));
  return {
    number: pr.number,
    created_at: pr.createdAt,
    merged_at: pr.mergedAt,
    open_to_merge_hours: Math.round(((Date.parse(pr.mergedAt) - Date.parse(pr.createdAt)) / HOURS) * 100) / 100,
    tier: tierFromLabels(pr.labels),
    changed_lines: pr.additions + pr.deletions,
    changed_files: pr.changedFiles,
    commits: nonMerge.length,
    commits_truncated: pr.commitsTotal > pr.commits.length,
    review_fix_commits: reviewFix.length,
    review_fix_subjects: reviewFix.map((commit) => commit.subject),
    ci,
    verdicts: tallyVerdicts(pr.comments),
    matt_comments: mattAll.filter((comment) => !isConventionComment(comment.body)).length,
    findings_by_tag: countBy(findings, (finding) => finding.tag),
    findings_by_disposition: countBy(findings, (finding) => finding.disposition),
    findings_by_reviewer: countBy(findings, (finding) => finding.reviewer),
    review_rounds: parseReviewRounds(pr.body) ?? NO_DATA,
    reviewed_minutes: minutes.length ? minutes.at(-1) : NO_DATA,
    signoff_sha: signoffs.at(-1) ?? NO_DATA,
  };
}

const sumCounts = (maps) => {
  const total = {};
  for (const map of maps) for (const [key, value] of Object.entries(map)) total[key] = (total[key] ?? 0) + value;
  return total;
};

/** Aggregate a field that only exists once a convention is in use: NO_DATA until any PR carries it. */
const conventionCounts = (records, field) => {
  const total = sumCounts(records.map((record) => record[field]));
  return Object.keys(total).length ? total : NO_DATA;
};

function verdictAggregate(records, kind) {
  const comments = sumCounts(records.map((record) => record.verdicts[kind].comments));
  const finals = countBy(records.filter((record) => record.verdicts[kind].final), (record) => record.verdicts[kind].final);
  return { comments, final_per_pr: finals, prs_without_comment: records.filter((record) => !record.verdicts[kind].final).length };
}

function ciAggregate(records, workflow) {
  const runs = records.map((record) => record.ci[workflow]);
  return {
    runs_per_pr: summarize(runs.map((run) => run.runs)),
    verdict_runs_per_pr: summarize(runs.map((run) => run.verdict_runs)),
    runs_total: runs.reduce((sum, run) => sum + run.runs, 0),
    failed_runs_total: runs.reduce((sum, run) => sum + run.failed, 0),
    cancelled_runs_total: runs.reduce((sum, run) => sum + run.cancelled, 0),
    prs_with_failed_run: runs.filter((run) => run.failed > 0).length,
    prs_with_no_run: runs.filter((run) => run.runs === 0).length,
  };
}

function mattMinutesByTier(records) {
  const reviewed = records.filter((record) => record.reviewed_minutes !== NO_DATA);
  if (reviewed.length === 0) return NO_DATA;
  const byTier = {};
  for (const record of reviewed) (byTier[record.tier ?? 'unlabelled'] ??= []).push(record.reviewed_minutes);
  const result = {};
  for (const [tier, minutes] of Object.entries(byTier)) result[tier] = { ...summarize(minutes), total: minutes.reduce((a, b) => a + b, 0) };
  return result;
}

function localRounds(records) {
  const values = records.map((record) => record.review_rounds).filter((value) => value !== NO_DATA);
  if (values.length === 0) return NO_DATA;
  return { prs_reporting: values.length, cap_hit: values.filter((value) => value === 'cap').length, rounds: summarize(values.filter((value) => value !== 'cap')) };
}

function noiseRate(byDisposition) {
  if (byDisposition === NO_DATA) return NO_DATA;
  const disposed = (byDisposition.fixed ?? 0) + (byDisposition.wrong ?? 0) + (byDisposition.intentional ?? 0) + (byDisposition['didnt-matter'] ?? 0);
  return share((byDisposition.wrong ?? 0) + (byDisposition['didnt-matter'] ?? 0), disposed);
}

export function aggregate(records, windowDays) {
  const commits = records.reduce((sum, record) => sum + record.commits, 0);
  const reviewFix = records.reduce((sum, record) => sum + record.review_fix_commits, 0);
  const withAtLeast = (n) => records.filter((record) => record.review_fix_commits >= n).length;
  const tiers = records.filter((record) => record.tier !== null);
  const findingsByDisposition = conventionCounts(records, 'findings_by_disposition');
  const perWeek = (count) => Math.round((count * 7 * 100) / windowDays) / 100;
  return {
    pr_count: records.length,
    prs_per_week: perWeek(records.length),
    open_to_merge_hours: summarize(records.map((record) => record.open_to_merge_hours)),
    changed_lines: summarize(records.map((record) => record.changed_lines)),
    changed_files: summarize(records.map((record) => record.changed_files)),
    commits_per_pr: summarize(records.map((record) => record.commits)),
    prs_with_truncated_commit_list: records.filter((record) => record.commits_truncated).length,
    review_fix: {
      commits: reviewFix,
      non_merge_commits: commits,
      share: share(reviewFix, commits),
      per_pr: summarize(records.map((record) => record.review_fix_commits)),
      prs_with_at_least: { 1: withAtLeast(1), 2: withAtLeast(2), 3: withAtLeast(3) },
      share_of_prs_with_at_least: { 1: share(withAtLeast(1), records.length), 2: share(withAtLeast(2), records.length), 3: share(withAtLeast(3), records.length) },
      per_week: perWeek(reviewFix),
    },
    ci: Object.fromEntries(REVIEWER_WORKFLOWS.map((workflow) => [workflow, ciAggregate(records, workflow)])),
    rounds_to_pass: {
      ci_correctness_runs: summarize(records.map((record) => record.ci['correctness-review'].runs)),
      ci_correctness_verdict_runs: summarize(records.map((record) => record.ci['correctness-review'].verdict_runs)),
      local: localRounds(records),
    },
    verdicts: Object.fromEntries(['correctness', 'grader', 'security'].map((kind) => [kind, verdictAggregate(records, kind)])),
    matt_comments: { total: records.reduce((sum, record) => sum + record.matt_comments, 0), prs_with_any: records.filter((record) => record.matt_comments > 0).length },
    findings: {
      by_tag: conventionCounts(records, 'findings_by_tag'),
      by_disposition: findingsByDisposition,
      by_reviewer: conventionCounts(records, 'findings_by_reviewer'),
      noise_rate: noiseRate(findingsByDisposition),
    },
    matt_minutes_by_tier: mattMinutesByTier(records),
    tier_distribution: tiers.length ? countBy(records, (record) => record.tier ?? 'unlabelled') : NO_DATA,
    tier2_signoffs: records.some((record) => record.signoff_sha !== NO_DATA) ? records.filter((record) => record.signoff_sha !== NO_DATA).length : NO_DATA,
  };
}
