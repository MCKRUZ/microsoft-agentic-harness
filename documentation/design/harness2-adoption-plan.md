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

| PR | Issue | Item | Layer |
|---|---|---|---|
| A | #840 | 5. Skill-validation rules | Infrastructure.AI, Domain.AI |
| B | #841 | 2. Evidence tripwires | Application.AI.Common, Infrastructure.AI |
| C | #842 | 6. Ledger, completion marker, call cache and audit, provider-pairing validator | Application.AI.Common, Infrastructure.AI |
| D | #843 | 1. Governed `IPatchProposer` and `IRolloutRunner` | Infrastructure.AI |
| E | #844 | 4. Provenance-blind rollout judging | Infrastructure.AI.Evaluation |
| F | #845 | 3. Structured experience library | Domain.AI, Application.AI.Common, Infrastructure.AI |
| G | #846 | 6. Offline deterministic demo and shared fakes | Tests.Common, Presentation.ConsoleUI |

Order: A (independent), then B and C, then D, then E and F, then G. B before D because the proposer consumes the evidence tree the tripwires guard; C before D because the session records into the ledger and marker; D before F because distillation runs on a stage record.

## Decisions taken

1. **Proposer session is read-only and returns the whole document.** `SkillDocumentDiff` derives edit ops, `PatchApplier` stays the only writer, and its dry-run must reproduce the returned text byte for byte.
2. **The proposer sees pass/fail only,** never partial credit or judge text. A documented deviation from SkillOpt.
3. **Experience entries live in `IKnowledgeMemory`** under domain-prefixed keys, not `ILearningsStore`; a later bridge can go through provenance.
4. **Plugin manifests that declare hooks are refused,** not given a hook loader.
5. **The MetaHarness fence gap is a separate issue** (#847); B wires the tripwires into both loops regardless.
6. **Non-delivered outputs** are excluded from the soft score (null, never zero) and count as hard zero.

## Controls and their guards

Every new control needs a caller and a test that fails when its guarding line is deleted (CLAUDE.md, "Shipping a control that nothing invokes"). Each issue lists the line and the test; the mutation check is part of the PR.

## Not verified

The `LearningEntry` fields; whether a provider-pairing validator already exists; whether the file tool honours a per-agent read-only grant; the exact single-agent `IAgentFactory` call path; the bodies of Harness²'s `proposer._write_audit` / `_paths` (the cache and audit design is taken from its call fingerprint and reply type). Nothing from Harness² was built or run.
