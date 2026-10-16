# Adopting ideas from Harness²

**Status:** plan, executed as PRs A to G (issues #840 to #846) plus issue #847.
**Source:** [google-research/harness2](https://github.com/google-research/harness2) (Apache-2.0, "Recursive Agent Harnessing for an Open World", arXiv 2610.14224). Read 15 October 2026; the paper abstract was read, the ablations were not. Its headline gains (up to 11.5 points on WorkBuddy-Bench, 4.5 on JobBench, 11.8 on LAB) are the authors' own, on three benchmarks.

## What Harness² is, and is not

Harness² improves an agent's harness; it is not a harness. For each task it runs the agent, asks a proposer for two edits (how the agent works, and what a practitioner knows), probes each candidate with a rollout, composes a final harness, and picks the best output with a blind LLM selector. A reflection step writes one lesson into a per-domain library for later tasks.

Our skill-training loop already covers part of that ground, with a verifier-based accept gate and a code-owned fence on what may be edited. The differences that matter:

| | Ours | Harness² |
|---|---|---|
| Editable surface | One thing: the skill document. Config knobs are suggest-only. Denied tools, autonomy tier, content-safety config and the fence itself are frozen by construction. | Eight component types, including plugins, hooks and scripts that run inside the tool loop. Nothing frozen. |
| Signal | Scored rollouts from a verifier | No ground truth; contrast between executions |
| Proposer | Fail-fast placeholder defaults | Agentic session reading evidence files |

**This work does not widen the fence and does not port Harness²'s editing of plugins, hooks or scripts.** Its proposer also runs with `--dangerously-skip-permissions`, its Codex executor with the sandbox off and no network restriction; ours goes through our own governance (content safety, token budgets, egress policy, the tool-call admission pipeline).

## Verified against our code (15 October 2026)

- No Infrastructure implementation of `IPatchProposer` / `IRolloutRunner` exists. Other implementations: private demo classes in `SkillTrainingExample.cs` and test stubs. MetaHarness already has an evidence-on-disk agentic proposer, `OrchestratedHarnessProposer`, whose dispatch path item 1 reuses.
- `TrainSkillCommandHandler` hands the proposer numeric `Hard` / `Soft` scores today, so a verdict is in the proposer's context.
- The MetaHarness handler files do not reference `EditableSurfaceRegistry` or `HarnessPatchValidator` (name-level reading). Tracked separately as #847.
- `SkillMetadataParser` defaults a missing description to an empty string and takes the name from the directory when absent; an unregistered tool name resolves to null with no log; `PluginManifest.Hooks` and `SkillDefinition.Scripts` have no non-test consumer.
- The trainer never calls `GetAsync` / `GetBestAsync` on its checkpoint store, so it cannot resume.
- Environment scrubbing for sandboxed processes already exists.
- Judges are pointwise; nothing selects among candidates with an LLM, so blind selection has no direct target.

## The six items and the PRs

The plan below was written first; the right-hand column records what each PR actually shipped, which is narrower in places and is the reason for the changes in the decisions list.

| PR | Issue | Item | Shipped as |
|---|---|---|---|
| A | #840 | 5. Skill-validation rules | #849: warnings only (blank description with frontmatter, unresolved `allowed-tools`, plugin hooks that never fire); no refusals, no name/script/untriggered checks |
| B | #841 | 2. Evidence tripwires | #850: pass/fail-only proposer view, grader-marker scan on every read path; MetaHarness not covered (score-driven by design) |
| C | #842 | 6. Resume | #851: resume from checkpoints only; no ledger or completion marker (a sequential loop cannot collide); no call cache or audit; no durable store (#853) |
| D | #843 | 1. Governed proposer and runner | #852 (proposer, read-only evidence tool) and #854 (rollout runner on the eval framework) |
| E | #844 | 4. Provenance-blind judging | #855: scorer-isolation scan and a provenance test only; no shuffle, cache or nullable soft score (see decision 6) |
| F | #845 | 3. Structured experience | #856: structured entries with code-derived verdicts, a library over the knowledge memory, a deterministic default distiller |
| G | #846 | 6. Offline demo and shared fakes | #857: the whole stack composed from real parts with two faked edges; shared scripted fakes |

Order: A (independent), then B and C, then D, then E and F, then G. B before D because the proposer consumes the evidence tree the tripwires guard; D before F because the experience is drawn from the steps the proposer produced. All but A and B are stacked, so each must be retargeted to `main` before its base is merged.

## Decisions taken

1. **Proposer session is read-only and returns the whole document.** `SkillDocumentDiff` derives edit ops, `PatchApplier` stays the only writer, and its dry-run must reproduce the returned text byte for byte. The reply is delimited by marker lines, not tags, because a skill that documents formats contains tags.
2. **The proposer sees pass/fail only,** never partial credit or judge text. A documented deviation from SkillOpt.
3. **Experience entries live in `IKnowledgeMemory`** under domain-prefixed keys, not `ILearningsStore`; a later bridge can go through provenance.
4. **Plugin manifests that declare hooks are warned about, not refused.** Changed from the original plan: refusing would break plugins that load fine today, and the hook paths are inert either way.
5. **The MetaHarness fence gap is a separate issue** (#847); B wires the tripwires into the skill-training loop only. Found on the way: `ToolDeclaration.Operations` is not enforced at call time, so a skill granted `file_system` can write whatever it declares, which is why the proposer gets a tool with no write operation.
6. **Non-delivered rollouts score zero, not "no score".** Changed from the original plan: excluding them from the soft mean lets a candidate raise a soft-only gate by failing on its hard items. "No score" is right for a judge that could not decide, which would need a nullable soft score through the gate and is not built.
7. **The default experience distiller is deterministic** (verdicts derived from the gate's decision), not a fail-fast placeholder: a placeholder would throw in every host that has the meta-skill update on. A model-backed distiller can use the same seam.
8. **Dataset items are assigned to splits by `split:<name>` tag**, with no default split, so a validation score is never silently a training score.

## Controls and their guards

Every new control needs a caller and a test that fails when its guarding line is deleted (CLAUDE.md, "Shipping a control that nothing invokes"). Each issue lists the line and the test; the mutation check is part of the PR.

## Not verified

Recall of earlier runs' lessons is a search through the knowledge memory and is tested against a fake memory only. The agent session and the agent turn are faked in every test; nothing here has been run against a real model or a real dataset. The `LearningEntry` fields were never needed. Nothing from Harness² was built or run.
