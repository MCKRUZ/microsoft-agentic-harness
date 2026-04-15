# Interview Transcript

## Q1: Should we build all 8 components in one go, or phase them?

**Answer:** All at once in one plan — single implementation plan covering all 8 items end-to-end, implement sequentially.

---

## Q2: What does 'scoring a harness candidate' mean for this project?

**Answer:** User-defined eval tasks (configurable). Score is computed by running the agent against a set of example tasks defined in config/files — flexible, task-agnostic.

---

## Q3: What agent should act as the harness proposer?

**Answer:** Reuse existing orchestrated agent infrastructure — wire the proposer through `RunOrchestratedTaskCommandHandler` with filesystem tool access. The proposer is just another agent config.

---

## Q4: Where should execution traces be stored on disk?

**Answer:** Configurable path via `MetaHarnessConfig`. Default to a `traces/` subdirectory relative to the app base path, overridable in `appsettings.json`.

---

## Q5: What tools should the proposer agent have access to when reading the trace filesystem?

**Answer:** All four:
- Existing `FileSystemService` (sandboxed read/search on the trace directory)
- `read_history` tool (new, exposes `IAgentHistoryStore` decision log)
- Shell/bash execution (sandboxed to trace directory — full shell within that boundary)
- MCP-exposed trace tools (trace filesystem exposed as MCP resources)

---

## Q6: How does a user trigger a harness optimization run?

**Answer:** Console UI command (`optimize`) that invokes `RunHarnessOptimizationCommand` via MediatR — consistent with existing ConsoleUI.

---

## Q7: Shell/bash scope — how restricted?

**Answer:** Full shell access within trace directory only. Any command allowed, but sandboxed to the trace directory via working directory restrictions.

---

## Q8: What does a harness candidate snapshot contain?

**Answer:** Skill files + system prompt + config snapshot. Capture the SKILL.md files, system prompt text, and relevant `AppConfig` values — the parts the proposer can actually change.

---

## Q9: When the optimization loop produces an improved candidate, how is it applied?

**Answer:** Write to disk, prompt user to review. Candidate improvements written to candidate directory. User reviews and manually promotes to active config.

---

## Q10: What happens when a candidate fails to evaluate?

**Answer:** Log failure, continue loop. Failed candidate is stored with error trace, `scores.json` records the failure, loop continues to next iteration with budget decremented.

---

## Q11: How far should SKILL.md parser changes go?

**Answer:** Extend existing parser, add new section types. Add `Objectives` and `TraceFormat` as first-class `SkillSection` types alongside existing tiers. Update `ISkillMetadataRegistry`.

---

## Q12: Test strategy for the outer optimization loop?

**Answer:** Both unit and integration. Unit tests for loop logic (mock `IHarnessProposer` + `IEvaluationService`), plus integration tests for the full trace store + candidate management flow using a temporary directory and scripted/canned proposer.
