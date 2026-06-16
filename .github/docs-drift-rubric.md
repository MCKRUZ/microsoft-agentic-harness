# Docs-drift rubric

The contract for the `docs-drift-check` workflow. A fresh agent reads a change that
landed on `main` and decides whether the documentation still tells the truth. If not,
it opens **one** PR with the minimal fix. It **advises** — a human merges the doc PR.

The one rule: **only flag drift a reader would actually hit.** A doc is stale when a
shipped, externally-visible behavior contradicts or is absent from a page that claims to
cover it. Internal refactors that change no documented behavior are **not** drift.

## What counts as drift (open a PR)

- A documented **CLI flag, command, config key, or env var** changed name, default, or
  semantics, and the page still shows the old form.
- A **workflow / CI gate** was added, removed, or changed in what it blocks/advises, and
  the delivery docs (see map) don't reflect it.
- A **new subsystem, feature, or public capability** shipped that a page is the natural
  home for, and the page is silent on it.
- A documented **file path, type name, or project** was moved/renamed/deleted and a page
  still points at the old location.
- A **count or version stated as fact** is now wrong (e.g. "three docs sites", "6 metrics",
  a pinned SDK version, a symbol count).

## What is NOT drift (do nothing)

- Internal refactors, renames of private members, test-only changes, formatting.
- Changes already accurately described by the existing prose.
- Aspirational/forward-looking content in the **Architecture guide** that intentionally
  describes a *target* Azure topology not yet built (e.g. the CI/CD section of
  `06-operations.html` describes a consumer's future deploy pipeline, not this repo's
  current rails). Only flag the Architecture guide when a statement is wrong *about its
  own stated scope*, not because the repo hasn't built it yet.
- A nice-to-have you'd merely *prefer* — if you wouldn't bet money a reader is misled,
  don't flag it.

## Doc map — where each topic lives

GitHub Pages sites (deployed by `.github/workflows/pages.yml`):

| Path | Deployed at | Covers |
| --- | --- | --- |
| `documentation/onboarding/` | `/` | Developer guide, chapters 00–15. The how-to-extend reference. |
| `documentation/architecture/` | `/architecture/` | **Target** Azure deployment topology, cost, ops. Aspirational by design. |
| `documentation/agentic-harness-course/` | `/agentic-harness-course/` | Conceptual course for non-technical readers. |
| `documentation/reference/` | `/reference/` | Patterns & technologies catalogue. |

Onboarding chapter → subsystem:

| Chapter | Subsystem / source area it tracks |
| --- | --- |
| `01-get-running` / `02-configuration` | setup, `appsettings.json` / `AppConfig` keys |
| `03-big-picture` / `04-message-journey` | Clean Architecture layers, MediatR pipeline behaviors |
| `05-skills` | skills system, `SKILL.md`, plugin-boundary governance |
| `06-tools` | `ITool`, keyed DI, sandboxing, MCP tool wrappers |
| `07-rag` | RAG pipeline (`Infrastructure.AI.RAG`) |
| `08-mcp` | MCP server & client |
| `09-patterns` | `Result<T>`, factories, immutability, validation |
| `10-observability` | OpenTelemetry, content safety, runtime governance |
| `11-extending` | add-a-tool / skill / agent / plugin recipes |
| `13-evaluation` | eval harness, metrics, reporters, `eval-suite.yml` |
| `14-skill-training` | SkillOpt port (`Application.AI.Common/.../SkillTraining`) |
| `15-delivery-governance` | the delivery rails: `ci.yml`, `grader.yml`, `security-review.yml`, `docs-drift-check.yml`, `RAILS.md`, branch protection, CODEOWNERS |
| `12-reference` | cheatsheet & glossary |

Non-Pages docs that may also need syncing: `documentation/design/`,
`documentation/security/`, `documentation/blueprints/`, `documentation/architecture/*.md`
(`magentic-spans.md`, `a2a-message-contract.md`). Prefer updating the Pages sites; touch
these only when the change is squarely theirs.

Delivery / CI changes (anything under `.github/`) map first to onboarding
`15-delivery-governance.html`, the `RAILS.md` gate table, and the reference catalogue's
"Delivery rails" bullet.

## House style — match the page you edit

These are hand-authored static HTML pages, no templating. To keep edits invisible:

- **Match the page's existing structure and tone.** Reuse its callout pattern
  (`<div class="callout callout-info|callout-warn|callout-danger">` with
  `callout-icon` / `callout-body` / `callout-title`), its tables, and its `<pre><code>`
  blocks. Don't introduce new components or CSS.
- **Nav is hand-coded per page and must stay identical across all sibling pages.** If you
  add or rename a chapter, update the `sidebar-nav` block in **every** onboarding page
  (00–15). Three nav formats exist (fully-compact one-line, semi-expanded, fully-expanded);
  match whichever the file already uses.
- **Cross-link with the right relative base.** Onboarding deploys to the site root, so
  onboarding→onboarding links are bare (`15-delivery-governance.html`). The reference and
  architecture sites are nested, so they link to onboarding via `../onboarding/NN-*.html`.
  Follow the conventions already in the file you're editing.
- Keep edits **minimal and surgical** — change only what drifted. No drive-by rewrites.

## Output

- **Drift found:** branch `docs/drift-sync-<7-char-sha>`, minimal edits under
  `documentation/**` only, one PR titled `docs: sync docs after <7-char-sha>`. The body
  lists, per file, what drifted and what changed. Never edit source, tests, or workflows.
- **No drift:** open nothing. Write a one-line note to `$GITHUB_STEP_SUMMARY` and stop.
  An empty or speculative PR is worse than silence.
