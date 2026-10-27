# Rails telemetry

Numbers for judging the review process, produced by one instrument,
`scripts/rails/collect-review-metrics.mjs`. Every regex and counting rule it uses is printed into each
output file under `definitions`. **Two files can be compared only when their `definitions` blocks are
identical.** If a definition changes, re-collect the baseline with the new script before comparing.

## Where the files are

| File | What it is |
| --- | --- |
| `.github/rails-telemetry/baseline-2026-10-27.json` (this folder, on `main`) | Pre-change baseline: PRs merged 2026-09-27 → 2026-10-27 (30 days, end exclusive). |
| `<ISO-week>.json` at the root of branch **`rails-telemetry`** | One file per ISO week (Monday 00:00 UTC to the next Monday), written by `.github/workflows/rails-telemetry.yml` every Monday at 05:17 UTC. |
| `windows/<since>_<until>.json` on branch `rails-telemetry` | Manual `workflow_dispatch` runs with an explicit window. |

`main` only accepts PRs, so the workflow commits to the orphan data branch `rails-telemetry`, creating
it on the first run. No ruleset matches that branch. Each run also uploads its file as the
`rails-telemetry` artifact (kept 90 days) and writes a digest to the job summary. A re-run of the same
window overwrites its own file.

Read the weekly files with `git fetch origin rails-telemetry` and then
`git show origin/rails-telemetry:2026-W44.json`.

Run the instrument locally (needs `gh` auth and an up-to-date `origin/main`):

```powershell
node scripts/rails/collect-review-metrics.mjs --since 2026-09-27 --until 2026-10-27 --out baseline.json
node --test scripts/rails/tests/*.test.mjs
```

## Comparing windows

- **Use equal-length windows.** For example, compare the 30-day baseline with a 30-day window, or
  compare weekly files with each other. Use `aggregates.prs_per_week` and the `per_week` fields when
  the lengths differ.
- **Compare per-PR figures, not totals.** Use medians, p90s, shares, and runs per PR. A busy week has
  more of everything.
- **Compare windows only when their `definitions` blocks match**, and check `warnings` first. A warning
  means some PR had more comments or threads than one page holds.
- Read the guardrails beside the efficiency numbers. If reverts or incidents go up while rounds go
  down, the time only moved somewhere else.

## The "no data" rule

`"no data"` means the convention behind a field has not been used in the window yet. Examples are
`Review-rounds:`, `Reviewed: <n> min`, finding markers, dispositions, tier labels, the `incident`
label, and an empty set to take a median of. It is never written for a real zero. A revert count read
from the full git history is a real `0`. A failed API call is never written as `"no data"` either: it
fails the run.

## Fields

### `window`, `instrument`, `definitions`

- `window`: `since`/`until` in UTC, `until` exclusive, `days`, and the ISO week of `since`.
- `instrument.window_end_commit`: the last first-parent commit on main before `until`. The workflow
  and ruleset files and `CLAUDE.md` are read at this commit.
- `definitions`: the frozen rules. See that block for the exact regexes.

### `aggregates` (all PRs merged in the window)

| Field | Meaning |
| --- | --- |
| `pr_count`, `prs_per_week` | Merged PRs, and that count × 7 / days. |
| `open_to_merge_hours`, `changed_lines`, `changed_files`, `commits_per_pr` | `{n, mean, median, p90, max}` per PR. Commits exclude merge commits. |
| `review_fix.share` | Review-fix commits / non-merge commits. A review-fix commit is one whose **subject says** it addresses review or CI-gate findings (`definitions.review_fix_commit`). |
| `review_fix.prs_with_at_least`, `share_of_prs_with_at_least` | PRs with ≥1 / ≥2 / ≥3 review-fix commits. |
| `ci.<workflow>` | Runs of `correctness-review`, `security-review` and `grader` matched to each PR by head branch, between the PR's open and merge times. `runs_per_pr` counts every run. `verdict_runs_per_pr` counts only runs that finished success or failure. Also: failed, cancelled and no-run totals. |
| `rounds_to_pass.ci_correctness_runs` | Correctness-review runs per PR: the CI rounds-to-pass. |
| `rounds_to_pass.local` | From the `Review-rounds: <n\|cap>` line in the PR body: how many PRs report it, how many hit the cap, and the rounds. |
| `verdicts.<reviewer>` | Bot verdict comments, counted per comment (every round) and as each PR's final verdict. `prs_without_comment` counts PRs where that reviewer posted nothing, for example a security review that short-circuited. |
| `matt_comments` | Comments, review bodies and inline comments by `MCKRUZ`, minus convention lines. **This is an upper bound**, because agents post with the same token. |
| `findings.by_tag`, `by_disposition`, `by_reviewer` | From the hidden `<!-- rail:... -->` markers on inline threads and the `Disposition:` replies. |
| `findings.noise_rate` | (wrong + didnt-matter) / dispositioned findings. |
| `matt_minutes_by_tier` | From `Reviewed: <n> min` comments, summarised for each tier label. |
| `tier_distribution`, `tier2_signoffs` | PRs per `tier:N` label, and PRs carrying a `Reviewed-by: @MCKRUZ <sha>` sign-off. |

### `guardrails`

- `reverts`: commits on main in the window whose subject starts with `Revert` or `revert:`.
- `incident_issues_opened`: issues created in the window that carry an `incident` or `incident:*` label.
- `open_breakers`: open `rails:breaker` issues when the run happened.

### `gates`

- `checks.required_not_declared`: a context the live ruleset requires that no PR-triggered workflow
  declares. Every PR would wait on it forever.
- `checks.required_not_observed`: a required context that no PR head in the window reported.
- `checks.live_not_committed` / `committed_not_live`: drift between the live ruleset and
  `.github/rulesets/*.json`.
- `checks.observed_not_required`: checks that ran on PRs but cannot block a merge, such as the
  grader, which is advisory by design. Review this list for a gate that ought to be required.
- `convention_labels`: whether each convention label exists.
- `claude_md_bytes`: the size of `CLAUDE.md` at `window_end_commit`.

### `telemetry_health`, `prs`

- `telemetry_health.missing_weeks_before_this_one`: weeks with no file between the earliest weekly
  file and this one. A skipped run shows up here on the next run.
- `prs`: the per-PR records the aggregates are computed from. `review_fix_subjects` lists exactly which
  commits counted.

## Known limits of the instrument

- **Review-fix share depends on how commit subjects are written.** The regex was tuned for precision.
  From 2026-10-05 (ISO week 41), commit subjects on main stopped naming reviews. The frozen regex
  matched 11.9% of main's non-merge commits in ISO week 40 and 2.4–7.4% in each week after it, and
  even the bare word "review" appears only 1–4 times a week. Subjects alone cannot tell whether rework
  fell or only stopped being named. Read the share next to `rounds_to_pass.ci_correctness_runs`, which
  counts pushes the reviewer had to look at again, whatever the subjects say.
- **Reconciling the earlier 24.7% and ~14%.** On the same 60 days of main's non-merge commits
  (2026-08-28 → 2026-10-27, 697 commits), the frozen regex matches 19.4% and the plan's broad regex
  matches 24.2%. The baseline's `review_fix.share` is lower again because it covers a different set:
  commits of PRs merged in its 30-day window, most of them after the phrasing change.
- **Control.** The same instrument, run on the 50 PRs merged 2026-10-19 → 2026-10-27, reproduces most
  of the figures hand-measured in the review-process plan (§0):

  | Figure | This instrument | Plan §0 |
  | --- | --- | --- |
  | correctness-review runs per PR (mean / median / p90 / max) | 2.04 / 2 / 3 / 4 | 2.02 / 2 / 3 / 4 |
  | grader comments (LOOKS GOOD / LOOKS RISKY) | 41 / 11 | 41 / 11 |
  | PRs over 1,000 changed lines | 21 | 21 |
  | correctness comments (CORRECT / DEFECTS FOUND) | 53 / 3 | 55 / 2 |
  | PRs with a failed correctness run | 4 | 3 |

  The last two rows are **unexplained**. Moving the 50-PR set back by one to six PRs does not reproduce
  them: it gives 51–52 / 3 and 4 in every case. Possible causes are that the plan matched tokens in
  comment bodies rather than classifying whole comments, or counted failures differently. The plan
  does not record its method, so neither can be checked.
- **Static workflow reading.** `declared` contexts come from a line-based reader of job names and
  inline matrix lists, not a full YAML parser. `observed` comes from real check runs and is the
  authoritative "does anything produce it".
