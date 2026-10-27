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
| **correctness-review** | `workflows/correctness-review.yml` | every PR; reviews when the diff touches `src/**` | **Blocks on a high-confidence defect** |
| **grader** | `workflows/grader.yml` | every PR | Advisory comment |
| **frontend (Presentation.Dashboard, Presentation.WebUI)** | `workflows/ci.yml` | every PR (no path filter) | **Blocks** — both jobs are required checks (live since before 2026-10-27; the committed ruleset JSON caught up that day) |
| **Documentation Structure Check** | `workflows/docs-links.yml` | PRs and pushes to main touching `documentation/**` | Fails the run on a dead link, stale anchor or sidebar drift; not required |
| **Dependency Audit** | `workflows/dependency-audit.yml` | PRs and pushes to main touching package manifests, plus a schedule | Fails the run on a vulnerable dependency outside the allowlist; not required |
| **docs-drift** | `workflows/docs-drift-check.yml` | push to main | Advisory comment |
| **AgentHub container build** | `workflows/agenthub-container.yml` | PRs touching `src/`, `skills/`, `agents/`; nightly | Advisory (not required). PRs build amd64 only unless the Dockerfile changes; nightly builds arm64 too |
| **Stop gate** | `../.claude/hooks/stop-build-gate.ps1` | agent tries to finish locally | **Blocks** a red build |
| **Human-only guard** | `../.claude/hooks/human-only-guard.ps1` | every agent `gh` or GitHub-MCP call | **Denies** the agent posting a tier-2 sign-off, applying an `accepted-risk:*` label, re-arming the `rails:breaker`, or merging a PR that is not `tier:0` |
| **Hook tests** | `workflows/hook-tests.yml` | PRs touching `.claude/hooks/**` or `.claude/settings.json` | Fails the run when any `.claude/hooks/tests/*.tests.ps1` suite fails; not required |

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
> **security-review and correctness-review are both *required* checks**, so disabling either would
> wedge every PR on a check that never reports. Both run on every PR and decide internally whether
> the expensive reviewer step is warranted — security-review from the diff's content
> (`.github/scripts/security-gate-scope.sh`), correctness-review from whether `src/**` changed.
> Expect both on most source PRs; that is the intended trade, not a misconfiguration.

Branch protection (`rulesets/main-branch-protection.json`) makes the six
blocking checks mandatory and requires a non-author approval + code-owner review.

The agent's `gh` is authenticated as Matt, so to GitHub an agent-made decision looks exactly like
his. The human-only guard is what keeps "an agent's approval never counts as a human's" true for
everything done through Claude Code. Its limits are stated in its header: it reads the tool
call's own text (and a `--body-file`), so a determined workaround is possible, and it does not
gate Matt's own terminal.

### Running the gates locally, before you push

`scripts/rails/run-gates.sh` runs those same gates on your machine — same diff
base, same changed-line anchors, same rubrics, same verdict protocol, same
applicability rules.

```bash
scripts/rails/run-gates.sh --list          # which gates apply to this branch, and why
scripts/rails/run-gates.sh                 # same as --fast (the default)
scripts/rails/run-gates.sh --fast          # compile/test gates only, no AI reviewers
scripts/rails/run-gates.sh --all           # every applicable gate, AI reviewers included
scripts/rails/run-gates.sh --correctness   # one named gate
scripts/rails/run-gates.sh --docs-drift    # advisory: what docs this change staled
```

Note that the reviewers diff **committed** state (`<base>...HEAD`), exactly as CI does.
Running the gate with work still in the working tree reviews the previous commit and will
happily report on code you have already changed — commit first, then run.

**`--fast` is the default (2026-08 change; made the script's actual no-flag default 2026-09-28).** `security-review` and
`correctness-review` are REQUIRED, blocking checks on every PR already — they run
remotely regardless of what happened locally first, so they are the real enforcement
boundary either way. Running the equivalent AI reviewers again locally, ahead of that,
duplicates coverage that's going to happen anyway: it doesn't raise the bar (the remote
gates re-derive their own verdict server-side, unaffected by any local run), and unlike
CI it has no `--max-turns` ceiling, so it can run arbitrarily long — measured once at
2.5+ hours against a mid-size diff, an order of magnitude slower than the CI round trip
it was meant to save. `--fast` (build/test/owasp/docs-links, plain dotnet/node, no local
`claude` subprocess) catches the cheap, obvious failures — won't build, fails a test —
in minutes, without competing with anything else running on the developer's machine
while it runs. `--all` still exists and is still worth it for a diff small enough that
the AI gates finish quickly; it's no longer the default expectation.

It is a pre-flight, **not** a replacement. It runs on a developer's machine with
their credentials, so the remote gates remain the enforcement boundary regardless —
do not disable the workflows on the strength of it. Its deliberate differences from
CI (no PR comment, no turn ceiling, local-only `--accept-risk`) are documented in
the script header.

One more thing is now gated on it running, though not cryptographically verified: a
clean run — `--fast` or the full applicable set, at the default base — writes its own
`run-gates` receipt (`.claude/.review-receipts/<fingerprint>.run-gates`), and
`.claude/hooks/review-gate.ps1` blocks `git push`/`gh pr create` through Claude Code
until that receipt exists for the exact diff being pushed — the same mechanism already
used for the `/code-review` and `/simplify` receipts (see review-cadence.md). Unlike
those two, the run-gates receipt is written by the script itself, not typed from memory,
so the intended path to producing it is an actual passing run. That is a convention this
hook makes the path of least resistance, not a technical guarantee: an agent with shell
access can still call the underlying writer by hand and skip the run entirely, exactly
as it could type a fake `/code-review` summary. What it does achieve is making the
honest path (run the gates, get the receipt for free) easier than the dishonest one,
which stops a fix-then-push rhythm from skipping even the cheap local pre-flight *by
default* and reaching CI with something that was never going to build. It does not, and
can't, guarantee any given fix is actually correct or complete, or that the receipt
wasn't hand-written — that's still on the reviewer (locally, and via `/code-review`/
`/simplify`) and on the remote gates re-deriving their own verdict (the actual
enforcement boundary for correctness and security).

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
4. **~~(Optional) Promote correctness-review to a required check.~~ Done 2026-07-30.**
   `correctness-review` is in the `required_status_checks` array of
   `rulesets/main-branch-protection.json` and live on `main`. A `CORRECTNESS_VERDICT: BLOCK`
   now stops the merge; the audited `accepted-risk:correctness` label is the only override.
   To reverse it, remove the context from the JSON and re-run the apply script — do not
   disable the workflow, which would leave the required check permanently unreported.

## Review conventions (a frozen interface)

Several rails emit text that other rails parse: the tier gate reads the PR body, the telemetry job
counts rounds and dispositions, the circuit breaker reads tier labels. These strings are an
interface. Change one only together with every reader, in the same PR.

| What | Exact form | Written by | Read by |
| --- | --- | --- | --- |
| Risk tier label | exactly one of `tier:0`, `tier:1`, `tier:2` | `tier-gate.yml` (computed from the change, never the author; the agent may not set one by hand) | telemetry, breaker, `human-only-guard.ps1` (merge rule) |
| Tier gate status | commit status context `tier-gate` on the PR head SHA | `tier-gate.yml` (recomputed on push, body edit, PR comment and finished Deep Review) | the `main-checks` ruleset |
| PR body sections | `## Intent`, `## Testing evidence`, `## Risk`, `## How to revert`, `## Explainer` (tier ≥1) | the agent, from `PULL_REQUEST_TEMPLATE.md` | `tier-gate.yml` (tier 1 needs Testing evidence and How to revert non-empty), grader, telemetry |
| Local review rounds | a line `Review-rounds: <n>` in the PR body (`<n>` = local `/code-review` rounds before the push; `cap` when the 3-round cap was hit) | `pre-push-review` skill | telemetry |
| Finding marker | hidden `<!-- rail:<reviewer> sha=<head7> anchor=<path>:<line> class=<kebab-class> tag=<important\|nit\|pre-existing> -->` as the first line of each inline review comment; `<reviewer>` is `correctness`, `security` or `deep-review` | the CI reviewers | the next review round (no repeats), telemetry |
| Severity tag | `[Important]`, `[Nit]`, `[Pre-existing]` at the start of the visible comment text | the CI reviewers | humans, telemetry |
| Disposition | a reply on the finding's thread: `Disposition: <fixed\|wrong\|intentional\|didnt-matter> — <reason>`, then the thread is resolved | the agent; `[Important]` may be closed as `wrong`, `intentional` or `didnt-matter` only on Matt's word | telemetry (noise rate) |
| Matt's review time | a PR comment `Reviewed: <n> min` | Matt, when he merges a tier-1 or tier-2 PR | telemetry |
| Tier-2 sign-off | a PR comment by the repository owner: `Reviewed-by: @<owner> <full 40-hex head commit id> — <one sentence on what was read>` (a short prefix could be ground to carry the approval onto other code; the gate posts a copy-paste line) | **Matt only** — `.claude/hooks/human-only-guard.ps1` denies any agent tool call that contains it | `tier-gate.yml` via `tier-status.mjs` |
| Circuit breaker | an **open** issue labelled `rails:breaker` | `tier-breaker.yml` (on a reverted tier-0 merge) | `tier-gate.yml` (no PR is classed tier 0 while one is open); Matt closes it to re-arm |

Receipt kinds under `.claude/.review-receipts/`: `code-review`, `simplify`, `run-gates`,
`round-cap` (written when the 3-round cap stops the loop; while it exists the push gate denies),
and `round-cap-ack` (Matt's verbatim "push anyway", which releases the cap; the writer refuses an
empty one). `-Round <n>` records which local round a receipt closes. A receipt may carry a findings array, one object per finding:
`{"anchor": "<path>:<line>", "class": "<kebab-class>", "tag": "important|nit|pre-existing", "disposition": "fixed|wrong|intentional|didnt-matter"}`.

## Risk tiers — how much of Matt's attention a change gets

Anthropic's Claude Code team scales human review by risk: none for low-risk changes once the
automated checks are green, one person for ordinary ones, two for the riskiest — and **the change
sets the tier, not the author**. On this solo repo the "people" column becomes Matt's attention:

| Tier | What it covers | What merges it |
| --- | --- | --- |
| **0** | Plain documentation; test files that only **add** lines | Green checks. The agent merges it (`gh pr merge --admin`); Matt does not read it |
| **1** | Every other change | Matt reads the PR body's evidence block (Testing evidence, Risk, How to revert, Explainer) and merges |
| **2** | `.github/**`, `.claude/settings.json`, `.claude/hooks/**`, `scripts/rails/**`, `**/Auth/**`, `**/Identity/**`, `**/Security/**`, `**/Migrations/**`, `infra/**`; anything the security gate wants reviewed; more than 1,000 changed lines under `src/` | A second, independent model review (`deep-review`) runs; Matt reads it and the evidence, then posts `Reviewed-by: @<owner> <full head commit id> — <what he read>` (the gate posts the exact line to copy). A new push needs a new sign-off. If the PR edits the deep review itself, that review cannot vouch for anything, so the gate stops waiting for it and the sign-off must cover the full diff |

`.github/scripts/tier-rules.mjs` is the single definition (unit-tested; keep its gated paths in
step with `CODEOWNERS`). It only ever raises a tier and never reads the PR description.
Deliberately **not** tier 0: an edit or deletion of an existing test, `CLAUDE.md`, `.claude/**`,
`skills/`, `agents/` and the published OpenAPI spec — the review loop and its guards live there.

What enforces it:

- `tier-gate.yml` labels every PR and publishes the required `tier-gate` status. It runs on
  `pull_request_target`, i.e. the **default branch's** copy, so a PR cannot rewrite the rules that
  grade it. That is safe only because it never executes PR code (see its header and "Gate
  integrity" below).
- The **circuit breaker**: if a tier-0 merge is ever reverted, `tier-breaker.yml` opens a
  `rails:breaker` issue and, while it is open, nothing is tier 0. Matt re-arms it by closing the
  issue. It recognises `gh pr revert` PRs and git's standard `This reverts commit …` message; a
  hand-worded revert is not detected, so open a breaker issue by hand for one.
- `human-only-guard.ps1` lets the agent merge only `tier:0` PRs, and never set a tier label, post
  a sign-off, or close the breaker.
- Depth follows the tier: `correctness-review` gets 120 turns and a wider brief on tier 2.

## Required status checks

Two rulesets, split so that the admin bypass a solo repo needs for approvals cannot also skip the
checks (with one ruleset it did — a `pull_request`-mode bypass actor bypasses every rule in it):

- **`main-checks`** (`rulesets/main-checks.json`) — **no bypass actors.** Requires these contexts
  green, every review thread resolved, no deletion, no force-push:
  - `build-and-test`
  - `OWASP Agentic Top-10 Gate`
  - `security-review`
  - `correctness-review`
  - `frontend (Presentation.Dashboard)`
  - `frontend (Presentation.WebUI)`
  - `tier-gate`
- **`main-branch-protection`** (`rulesets/main-branch-protection.json`) — the 1-approval +
  code-owner rule, with the admin bypass (see "Solo-repo accommodation").

Every required context in `main-checks` is pinned to the GitHub Actions app (`integration_id:
15368`, read from a live check run). Without the pin, anyone holding a token with `statuses:
write` — including the agent, whose `gh` is Matt's — could post `tier-gate: success` and skip the
tier-2 sign-off; the newest status for a context wins. The pin rejects statuses posted with a
personal token. It does **not** stop a workflow inside a same-repo PR from posting one with its
own `GITHUB_TOKEN`; `human-only-guard.ps1` denies the agent writing such a workflow (any workflow
but `tier-gate.yml` that holds `statuses: write` or names the `tier-gate` context), and such a PR
is tier 2 by its paths. The shakedown below must confirm the pin accepts `tier-gate.yml`'s own
statuses before the split is trusted; if it does not, use the recovery switch and drop the pin.

`tier-gate` can only be required once `tier-gate.yml` is on `main` (it runs the default branch's
copy); apply the split after that PR merges, never before, or every PR waits on a status nobody
sets.

**Recovery.** With no bypass on `main-checks`, a rail that cannot report — most likely an expired
`CLAUDE_CODE_OAUTH_TOKEN`, which makes the AI reviewers fail closed — blocks every merge,
including the admin's. Repository admins can always edit rulesets:

```bash
scripts/rails/apply-branch-protection.sh --disable main-checks   # merge the fix, then:
scripts/rails/apply-branch-protection.sh --enable main-checks
```

The live rulesets are the authority; the JSON files must match them exactly. On 2026-10-27 they
did not (live required the two frontend jobs and `require_extra_approval_for_unattributed_changes`;
the JSON had neither), so re-running the apply script would have silently dropped two required
checks. Check parity before any apply:

```bash
N='{bypass_actors, rules: [.rules[] | {type, parameters: (.parameters // null)}]}'
for f in .github/rulesets/*.json; do
  id=$(gh api repos/MCKRUZ/microsoft-agentic-harness/rulesets --jq ".[] | select(.name==\"$(jq -r .name "$f")\") | .id")
  diff <(jq -S "$N" "$f") <(gh api "repos/MCKRUZ/microsoft-agentic-harness/rulesets/$id" | jq -S "$N") && echo "parity: $f"
done
```

A second live ruleset, `Protection` (id 16093641: deletion + non-fast-forward, `include: []`),
targets no branch and therefore does nothing. It is not managed here; it is recorded so nobody
mistakes it for the main-branch protection.

Overrides, both audited on the PR timeline and both Matt's alone (the human-only guard denies the
agent applying them): `accepted-risk:correctness` and `accepted-risk:security` accept a **known**
finding. Neither overrides a review that did not finish (a provisional block).

The grader is intentionally **not** required — it advises the human Checker.

Every required check must **report on every PR**, including ones it has nothing to say about.
That is why none of the four carries a `paths:` trigger filter: a workflow that does not run
leaves its required context pending forever and the PR cannot be merged at all. Each decides
internally whether to do expensive work and short-circuits to success otherwise. Never "optimise"
one of these by adding a path filter to its trigger.

## Solo-repo accommodation (read this)

GitHub forbids approving your own PR, so on a single-maintainer repo the
"non-author approval" rule cannot be self-satisfied. The `main-branch-protection`
ruleset is configured **armed with an owner bypass**: it requires 1 approval +
code-owner review, but the repository-admin role is a bypass actor with
`bypass_mode: pull_request`, so you can still self-merge your own PRs today. Since
2026-10-27 that bypass waives **only** the approval: the required checks live in
`main-checks`, which has no bypass, so even the owner cannot merge a red PR. (Before
the split, the one ruleset held both, and the bypass skipped the checks too — the
prose here said otherwise.)

What stands in for the missing second human is the tier system above: the change
decides whether Matt reads nothing, reads the evidence, or reads an independent
review and signs off on a specific commit. It never stands in for Matt himself, and
an agent's approval never counts as his (`human-only-guard.ps1`).

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

The one exception is `tier-gate.yml`, and it is an exception in the safe direction. A gate that
decides how much human review a change gets must not run the PR's own copy of its rules, or a PR
could lower its own tier — so it runs the default branch's copy on `pull_request_target`. It is
safe there only because it executes nothing from the PR: it checks out the base branch, reads the
PR head as git objects (`git diff`), runs only base-branch scripts, reads the PR body through the
API (never interpolated into a shell line), and holds no secret beyond a `GITHUB_TOKEN` scoped to
labels and statuses. Any future step that checks out, builds or runs PR code there reopens the
hole this note describes. `deep-review.yml` runs the AI reviewer and therefore stays on
`pull_request`, like the other reviewers.

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
  itself, by replaying every PR the gate is known to have missed: the six the
  folder-only filter skipped (#203–#210), #215's path confinement, #227's dead
  tool-permission control, and #226's rewrite of the push gate. Each miss was a
  category the trigger had no word for, so a new miss belongs in that suite before
  it is fixed.
- **correctness-review** — open a throwaway PR with a planted high-confidence
  defect under `src/` (e.g. an inverted null check or an off-by-one that drops a
  row). The check must go red with `CORRECTNESS_VERDICT: BLOCK`; then apply the
  `accepted-risk:correctness` label and confirm it goes green. Close it unmerged.
  Do this before promoting it to a required check (go-live step 4).
- **CI / OWASP** — already exercised by every real PR.
- **tier-gate** (after the ruleset split is applied) — (1) a docs-only throwaway PR must be
  labelled `tier:0`, go green, and merge with `gh pr merge --admin`; (2) a PR touching
  `.github/workflows/ci.yml` must be `tier:2`, wait on `deep-review`, stay pending until the
  owner posts a sign-off naming its head commit, then go green — and go pending again after one
  more push; (3) a `src/` PR with the template left empty must be `tier:1` and red until Testing
  evidence and How to revert are filled in; (4) `gh pr merge --admin` on a red PR must be refused
  (proves `main-checks` has no bypass). Unit coverage: `node --test ".github/scripts/tests/*.test.mjs"`.
- **circuit breaker** — revert a throwaway tier-0 merge with `gh pr revert`, run the Tier Breaker
  workflow, and confirm a `rails:breaker` issue opens and the next docs-only PR is `tier:1`.

## Deferred (not built — no cloud deployment exists yet)

Deploy/promotion pipeline, rollback rehearsal, IaC (Bicep) + its what-if/policy
funnel, production Key Vault secret rotation, and the `specs/` system. Revisit
when the project gains cloud infrastructure (the-rails.md §5–§6, §8).
