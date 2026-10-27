// Every regex and rule a number in the telemetry file depends on, printed verbatim into the file so
// two windows can only be compared when their `definitions` blocks are identical.

import * as parsers from './review-parsers.mjs';
import * as verdicts from './bot-verdicts.mjs';
import { PERCENTILE_DEFINITION } from './stats.mjs';
import { HUMAN_REVIEWER, REVIEWER_WORKFLOWS } from './pr-records.mjs';

const show = (regex) => String(regex);

export const DEFINITIONS_VERSION = 1;

export function buildDefinitions() {
  return {
    version: DEFINITIONS_VERSION,
    window: 'PRs whose mergedAt is in [since, until) UTC; per_week values = count * 7 / window days',
    review_fix_commit: {
      rule: 'a non-merge PR commit (parents < 2) whose subject matches scope OR whose description (subject minus type prefix) matches description',
      subject: parsers.COMMIT_SUBJECT_RULE,
      scope: show(parsers.REVIEW_FIX_SCOPE),
      type_prefix: show(parsers.COMMIT_TYPE_PREFIX),
      description: show(parsers.REVIEW_FIX_DESCRIPTION),
      share: 'review-fix commits / non-merge commits, over all PRs merged in the window',
    },
    revert: { regex: show(parsers.REVERT_SUBJECT), source: 'git log <ref> --since --until (all commits reachable from main, not first-parent)' },
    incident_label: show(parsers.INCIDENT_LABEL),
    weekly_file: show(parsers.WEEKLY_FILE),
    ci_runs: {
      workflows: REVIEWER_WORKFLOWS.map((workflow) => `.github/workflows/${workflow}.yml`),
      matching: 'workflow runs with event=pull_request, head_branch = PR head branch, created_at in [PR createdAt, PR mergedAt]',
      runs: 'every matched run, any conclusion (latest attempt only, as the runs API lists it)',
      verdict_runs: 'matched runs that concluded success or failure (excludes cancelled-by-concurrency and skipped)',
      rounds_to_pass_ci:
        "ci_correctness_runs = correctness-review runs per PR, any conclusion (the definition behind the review-process plan's §0 figure of 2.02); ci_correctness_verdict_runs = verdict_runs only",
    },
    verdict_comments: {
      author_logins: [...verdicts.BOT_LOGINS],
      kind: Object.fromEntries(Object.entries(verdicts.COMMENT_KIND).map(([kind, regex]) => [kind, show(regex)])),
      correctness: {
        order: 'inline Blocking-defects section, else heading Blocking-defects section ("none" = correct, any other content = defects-found), else first token',
        inline_section: show(verdicts.CORRECTNESS_INLINE_SECTION),
        heading_section: show(verdicts.CORRECTNESS_HEADING_SECTION),
        section_none: show(verdicts.CORRECTNESS_NONE),
        token: show(verdicts.CORRECTNESS_TOKEN),
      },
      grader: show(verdicts.GRADER_TOKEN),
      security: show(verdicts.SECURITY_TOKEN),
      final_per_pr: 'verdict of the last comment of that kind on the PR',
    },
    conventions: {
      tier_label: show(parsers.TIER_LABEL),
      review_rounds: show(parsers.REVIEW_ROUNDS_LINE),
      reviewed_minutes: `${show(parsers.REVIEWED_MINUTES_LINE)} in a PR comment by human_reviewer; last one wins`,
      signoff: show(parsers.SIGNOFF_LINE),
      finding_marker: `${show(parsers.FINDING_MARKER)} on the first comment of an inline review thread`,
      severity_tag: show(parsers.SEVERITY_TAG),
      disposition: `${show(parsers.DISPOSITION_LINE)} in any reply on the finding's thread; last one wins`,
    },
    noise_rate: '(wrong + didnt-matter) / (fixed + wrong + intentional + didnt-matter), over marker-carrying findings',
    human_reviewer: HUMAN_REVIEWER,
    matt_comments: {
      rule: 'issue comments, non-empty review bodies and inline comments authored by human_reviewer, minus those matching convention_comment. UPPER BOUND: agents post with the same token, so their comments are counted too',
      convention_comment: show(parsers.CONVENTION_COMMENT),
    },
    no_data: `"${parsers.NO_DATA}" means the convention that produces the value has not been used in the window. It is never written for a failed API call (the run fails instead) and never for a real zero`,
    percentile: PERCENTILE_DEFINITION,
    checks: 'required = live rules/branches/main and committed .github/rulesets/*.json targeting the default branch; declared = static read of pull_request-triggered workflow job names (matrix expanded) plus literal commit-status contexts; observed = check runs and statuses on the head commit of every PR in the window',
    claude_md_bytes: 'size of CLAUDE.md at the last first-parent commit on main before until',
  };
}
