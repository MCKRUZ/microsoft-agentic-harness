# The delivery rails

This repo's CI/CD + DevOps governance, built to the **Intent Driven Development**
delivery standard. The spec is `delivery-standard/docs/the-rails.md`; the one rule
everything hangs off is **the agent proposes, a gate disposes**.

This page is the operator's guide: what the gates are, what you must do to take
them live, and how to prove they actually catch things.

## The gates

| Gate | File | Fires on | Blocks or advises |
| --- | --- | --- | --- |
| **build-and-test** | `workflows/ci.yml` | every PR | **Blocks** (hard gate) |
| **OWASP Agentic Top-10 Gate** | `workflows/ci.yml` | every PR | **Blocks** (hard gate) |
| **security-review** | `workflows/security-review.yml` | every PR; reviews when the diff contains security-relevant code, touches a gated path, or carries `risk:high` | **Blocks on HIGH** |
| **correctness-review** | `workflows/correctness-review.yml` | every PR | Advisory comment |
| **grader** | `workflows/grader.yml` | every PR | Advisory comment |
| **docs-drift** | `workflows/docs-drift-check.yml` | push to main | Advisory comment |
| **Stop gate** | `../.claude/hooks/stop-build-gate.ps1` | agent tries to finish locally | **Blocks** a red build |

> **All four AI rails run on the Claude subscription, not metered API credits.** Every one of
> them passes `claude_code_oauth_token: ${{ secrets.CLAUDE_CODE_OAUTH_TOKEN }}` and **no**
> `anthropic_api_key`. That is the whole reason they can be enabled at all: correctness-review,
> grader and docs-drift were previously disabled at the GitHub level because they billed
> `ANTHROPIC_API_KEY` and re-trigger on every push to a PR branch, so a fix-then-push rhythm paid
> for the Opus correctness reviewer several times per PR. Same reviewers, same rubrics, different
> pot — the one `scripts/rails/run-gates.sh` already used locally.
>
> **Do not add `anthropic_api_key` back as a fallback.** The action forwards both credentials to
> the CLI and does not itself decide precedence, so passing both leaves the billing pot undefined
> and silently reintroduces the metered spend. A missing or rejected token must fail loudly.
>
> `run-gates.sh` is now a genuine pre-flight rather than a substitute: it catches findings before
> a PR cycle, but the remote rails are the enforcement boundary again. Correctness and doc-drift
> coverage is no longer on the honour system.
>
> **security-review is the only one that is also a *required* check**, so disabling it would wedge
> every PR on a check that never reports. Its expensive step fires when the diff is actually
> security-relevant — see `.github/scripts/security-gate-scope.sh`. Expect it on most `src/**` PRs;
> that is the intended trade, not a misconfiguration.

Branch protection (`rulesets/main-branch-protection.json`) makes the three
blocking checks mandatory and requires a non-author approval + code-owner review.

### Running the gates locally, before you push

`scripts/rails/run-gates.sh` runs those same gates on your machine — same diff
base, same changed-line anchors, same rubrics, same verdict protocol, same
applicability rules.

```bash
scripts/rails/run-gates.sh --list          # which gates apply to this branch, and why
scripts/rails/run-gates.sh                 # every applicable gate
scripts/rails/run-gates.sh --fast          # compile/test gates only, no AI reviewers
scripts/rails/run-gates.sh --correctness   # one named gate
scripts/rails/run-gates.sh --docs-drift    # advisory: what docs this change staled
```

Note that the reviewers diff **committed** state (`<base>...HEAD`), exactly as CI does.
Running the gate with work still in the working tree reviews the previous commit and will
happily report on code you have already changed — commit first, then run.

Two reasons to use it. **Cost:** the remote reviewers re-trigger on *every* push to the PR
branch, so a fix-then-push rhythm pays for the expensive Opus reviewers two or three times per
PR. Both now bill the same Claude subscription, so this is a question of how much of your
subscription allowance a PR consumes rather than which pot it comes from. Clear the gates
locally, push once, pay for one remote cycle. **Latency:** a local BLOCK arrives in minutes
without burning a PR cycle.

It is a pre-flight, **not** a replacement. It runs on a developer's machine with
their credentials and nothing verifies it ran, so the remote gates remain the
enforcement boundary — do not disable the workflows on the strength of it. Its
deliberate differences from CI (no PR comment, no turn ceiling, local-only
`--accept-risk`) are documented in the script header.

## Go-live — what a human must do (not automatable from here)

These are deliberate, outward-facing actions. Nothing in this PR performs them.

1. **Install the Claude GitHub App** on the repo: run `/install-github-app` in
   Claude Code, or install from <https://github.com/apps/claude>. Needed for the
   grader, security-review, and docs-drift workflows. (Repo admin required.)
2. **Add the `CLAUDE_CODE_OAUTH_TOKEN` repository secret.** Mint it in a real terminal with
   `claude setup-token` (it needs an interactive TTY — running it inside an agent session
   produces no output), then `gh secret set CLAUDE_CODE_OAUTH_TOKEN` and paste at the prompt so
   the value never reaches your shell history. All four AI rails authenticate with this and
   nothing else, so the spend lands on the Claude subscription. Until it is set, the
   security-review and correctness-review gates **fail closed on the PRs they review** (by
   design) and the grader stays green/no-op. `ANTHROPIC_API_KEY` is no longer used by any
   workflow — see the note under the gates table before adding it back.
3. **Apply branch protection** once you've read the desired ruleset:
   ```bash
   scripts/rails/apply-branch-protection.sh --dry-run   # review the plan
   scripts/rails/apply-branch-protection.sh             # apply (prompts to confirm)
   ```
   This is the only sanctioned way to change branch protection — edit the JSON,
   re-run the script. Do not hand-edit rules in the GitHub UI.
4. **(Optional) Promote correctness-review to a required check.** It ships wired
   and fail-closed but is intentionally **not** in the ruleset, so merging it does
   not wedge every source PR before steps 1–2 are done. Once the Claude App + key
   are live and you've watched it run on a few PRs, add `correctness-review` to the
   `required_status_checks` array in `rulesets/main-branch-protection.json` and
   re-run the apply script. Until then it advises (its red X does not block).

## Required status checks

The ruleset requires exactly these check contexts to be green before merge:

- `build-and-test`
- `OWASP Agentic Top-10 Gate`
- `security-review`

The grader is intentionally **not** required — it advises the human Checker.

## Solo-repo accommodation (read this)

GitHub forbids approving your own PR, so on a single-maintainer repo the
"non-author approval" rule cannot be self-satisfied. The ruleset is configured
**armed with an owner bypass**: it requires 1 approval + code-owner review, but
the repository-admin role is a bypass actor with `bypass_mode: pull_request`, so
you can still self-merge your own PRs today. `pull_request` (not `always`) is
deliberate: even the owner cannot push *directly* to `main` skipping CI — every
change to `main` still rides a PR and its checks; the bypass only waives the
human-approval requirement that a solo repo can't satisfy.

This is honest, not hidden: the human-review rule is fully wired and becomes real
the moment a second collaborator (or a review bot) joins — at that point, **remove
the bypass actor** from `main-branch-protection.json` and re-apply. Until then,
treat the bypass as the methodology's deliberately-expensive escape hatch
(the-rails.md §4), not as routine.

## Gate integrity (known residual risk)

These workflows trigger on `pull_request`, which runs the **PR's own copy** of the
workflow, the rubric, and the gated-path regex. For a same-repo branch that runs
with secrets, a PR could in principle weaken its own gate (rewrite the rubric to
force `PASS`, edit the regex to exclude its path, neutralize the enforce step).
Mitigations in place: the verdict file is written/read outside the working tree
and any committed copy is deleted before review (so a planted `PASS` can't pass);
and changes to `.github/**` are themselves a gated path requiring code-owner
review. The real closure is a **non-author review of rails changes** — which is
exactly what branch protection enforces once a second reviewer exists (remove the
owner bypass then). **Never** switch these workflows to `pull_request_target`:
that would expose secrets and the write token to forked-PR code.

## Prove the rails (the shakedown — the-rails.md §9)

> A pipeline that has never caught anything is not proven — it is merely present.

Before trusting these, force each one to fail and confirm it's caught:

- **Stop gate** — break a `.cs` file under `src/`, then try to end a Claude Code
  turn. The Stop hook must refuse and hand back the build error.
- **grader** — open a PR whose description claims something the diff does not do.
  The grader's comment must call out the mismatch.
- **security-review** — open a throwaway PR with a planted HIGH issue, either on a
  gated path (a comment in a file under `**/Auth/`) or on the content signal (any
  `src/**` change touching `OwnerId`/`[Authorize]`). The check must go red. Close it
  unmerged. `bash .github/scripts/security-gate-scope.test.sh` verifies the trigger
  itself — including replaying the six PRs the old folder-only filter missed.
- **correctness-review** — open a throwaway PR with a planted high-confidence
  defect under `src/` (e.g. an inverted null check or an off-by-one that drops a
  row). The check must go red with `CORRECTNESS_VERDICT: BLOCK`; then apply the
  `accepted-risk:correctness` label and confirm it goes green. Close it unmerged.
  Do this before promoting it to a required check (go-live step 4).
- **CI / OWASP** — already exercised by every real PR.

## Deferred (not built — no cloud deployment exists yet)

Deploy/promotion pipeline, rollback rehearsal, IaC (Bicep) + its what-if/policy
funnel, production Key Vault secret rotation, and the `specs/` system. Revisit
when the project gains cloud infrastructure (the-rails.md §5–§6, §8).
