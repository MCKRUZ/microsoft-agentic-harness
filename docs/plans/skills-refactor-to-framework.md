# Skills Subsystem: Alignment with Microsoft Agent Framework

**Status:** Plan rewritten 2026-08-01 against verified evidence. **Phase 1 and Phase 3 are done; Phase 2 is not started.** See §5 for what shipped and the extra defect the work uncovered.
**Supersedes:** the original version of this file, which was written against `Microsoft.Agents.AI` 1.0.0-rc4 and is now substantially wrong (see §1).
**Installed SDK:** `Microsoft.Agents.AI` **1.13.0** (`src/Directory.Packages.props:31`).
**Related:** GitHub issue #219 (MCP-based skill discovery) — see §8.

---

## 0. Headline

**The original plan's central premise — "replace our skill model and parser with the framework's" — is not viable, and pursuing it would be a net downgrade.** The framework's file parser provably cannot represent our skill manifests, and two capabilities we depend on (runtime tool enforcement, token budgeting) do not exist in the framework at all.

**However, the investigation surfaced a real defect worth fixing:** the harness eagerly injects full skill bodies into the system prompt while *simultaneously* wiring the framework's on-demand loader over the same skills. Progressive disclosure — the harness's own documented headline architecture — is effectively switched off. See §5.

The recommended work is therefore much narrower than the original plan, and it moves in a different direction: **extend the framework at its sanctioned seam rather than replace our layer with it.**

---

## 1. What the previous version of this plan got wrong

Recorded so it is not re-derived. All verified by whole-tree grep over `src/`.

### 1a. Files it said to delete that no longer exist

Six of the ten deletion targets have **zero references anywhere in `src/`** — they were removed in earlier work:

| Symbol | Reality |
|---|---|
| `ISkillContentProvider` | Does not exist |
| `FileSystemSkillContentProvider` | Does not exist |
| `CandidateSkillContentProvider` | Does not exist |
| `SkillCacheStatistics` | Does not exist |
| `ContextLoading` | Does not exist |
| `ContextContract` | Does not exist |
| `SkillReference` | Does not exist |
| `SkillsContextProviderFactory` | Does not exist |
| `ITieredContextAssembler` / `TieredContextAssembler` | **No code.** Only stale prose in `src/Content/Application/Application.AI.Common/README.md` (lines 127, 175, 287, 332, 333) |

**Consequence:** the plan's headline "~875 fewer lines" is unachievable — roughly 485 of those lines were already deleted. That saving is already banked.

### 1b. Its central blocker is obsolete, but for a reason that no longer helps

The old plan states: *"`FileAgentSkillLoader` is `internal` in the 1.0.0-rc4 SDK. We CANNOT use it directly."*

That is no longer true — but the type is gone entirely. In 1.13.0 the framework was **redesigned**, not merely renamed:

| rc4 (plan-era) | 1.13.0 (today) |
|---|---|
| `FileAgentSkill` | `AgentFileSkill` (public sealed, **internal constructor**) |
| `FileAgentSkillLoader` (internal) | `AgentFileSkillsSource` (public sealed) |
| `FileAgentSkillsProvider` | `AgentSkillsProvider` |
| `SkillFrontmatter` | `AgentSkillFrontmatter` |
| — | `AgentSkillsSource` (new public extension base) |
| — | Caching / Deduplicating / Filtering decorators (new) |
| — | `AgentClassSkill<T>`, `AgentInlineSkill`, skill attributes (new; rc4 had these Python-only) |

Every class name in the old plan's code samples and file tables is wrong.

### 1c. Two claims in the survey of this work that were themselves wrong

Flagged because they were nearly acted on:

- **`Domain.AI/Constants/SkillSources.cs` is not orphaned.** It is referenced by `SkillNotFoundException.cs:42` (XML doc `cref`) and belongs to a deliberate `Constants/` family alongside `SafetyCategories.cs` and `McpTransports.cs`. The duplication with `Domain.AI/Skills/SkillSources.cs` is real (§6c) but it is not a stray file to delete unexamined.
- **The framework provider does not re-emit skill bodies into the system prompt.** `AgentSkillsProvider.BuildSkillsInstructions` emits only `<name>` and `<description>`. The full body arrives only if the model calls `load_skill`. This narrows — but does not eliminate — the double-load (§5).

---

## 2. Verified facts: the 1.13.0 skills API

Read from source at tag `dotnet-1.13.0` in `microsoft/agent-framework`. Namespace is flat `Microsoft.Agents.AI`.

### 2a. The extension seam (this is the important part)

```csharp
public abstract class AgentSkillsSource : IDisposable
{
    public abstract Task<IList<AgentSkill>> GetSkillsAsync(
        AgentSkillsSourceContext context, CancellationToken cancellationToken = default);
    protected virtual void Dispose(bool disposing) { }
}
```

One abstract method. Public, non-sealed, implicit public constructor. Wired in via:

```csharp
builder.UseSource(AgentSkillsSource source)                              // shared instance
builder.UseSource(Func<ILoggerFactory?, AgentSkillsSource> factory)      // per-Build() instance
```

`AgentSkillsSourceContext` carries `AIAgent Agent` and `AgentSession? Session`, so per-agent and per-session skill sets are supported.

**Microsoft's own MCP skills package extends the system through exactly this seam** — an extension method on the builder wrapping an `internal` `AgentSkillsSource` subclass. This is the sanctioned pattern, confirmed by first-party usage.

### 2b. `Build()` pipeline — fixed order, not extensible

```
Aggregating → Caching → Filtering → Deduplicating → AgentSkillsProvider
```

Caching sits *inside* Filtering (filter re-runs per request against a cached list). Deduplication is unconditional. There is **no hook to insert a custom decorator into this chain** — you must wrap your own source before handing it to `UseSource`. `DelegatingAgentSkillsSource` is public and abstract if you want to write one.

### 2c. What the model actually sees

`AgentSkillsProvider` is an `AIContextProvider`. Per invocation it re-queries the source and returns instructions + three tools:

| Tool | Behavior |
|---|---|
| `load_skill` | Returns the full SKILL.md body for a named skill |
| `read_skill_resource` | Reads a named resource file |
| `run_skill_script` | Runs a named script |

All three are **approval-required by default** (`ApprovalRequiredAIFunction`); three `Disable*Approval` options and two static auto-approval rules exist.

The system-prompt block renders, per skill, **only**:

```xml
<skill>
  <name>...</name>
  <description>...</description>
</skill>
```

`AllowedTools`, `License`, `Compatibility` and `Metadata` are **never rendered**, and there is no hook to change per-skill rendering. A custom `SkillsInstructionPrompt` can only change the surrounding template, not the per-skill shape.

### 2d. Frontmatter parsing — the blocking constraint

`AgentFileSkillsSource` promotes exactly **five** top-level keys: `name`, `description`, `license`, `compatibility`, `allowed-tools`.

**There is no `else` branch. Every other top-level key is matched, assigned to a local, and silently discarded — no warning, no logging.**

Custom data is only captured from an explicit `metadata:` block, and that block is parsed by a flat indented-key/value regex:

- **Flat only** — nested maps and lists are not modelled
- **String values only** — no type coercion
- Only the **first** `metadata:` block matches

Two further hard constraints, both `private const` / hard-coded:
- Skill filename must be `SKILL.md`
- Directory search depth is 2
- **Frontmatter `name` must equal the containing directory name** (ordinal), or the skill is rejected

### 2e. Two definitive absences

| Capability | Present in 1.13.0? |
|---|---|
| Token / context-budget accounting | **No.** Grepped all 42 files: every `token` hit is `CancellationToken`, a doc placeholder, or an archive byte-budget for zip-bomb defense. No tokenizer, no counting, no truncation. |
| Runtime `allowed-tools` enforcement | **No.** `AllowedTools` has one declaration and two writes. **Zero reads.** It is never consulted, never rendered, never compared against `AIContext.Tools`. It is decorative metadata. |

The only runtime gate is per-tool human approval on the three skill tools — which gates *the skill tools*, not *which tools a skill may use*.

---

## 3. Verified facts: our subsystem today

### 3a. Where the framework is already used — three call sites only

| Location | What |
|---|---|
| `Application.AI.Common/Factories/AgentExecutionContextFactory.cs:35-37` | `NoOpScriptRunner` — returns null. **Skill scripts are disabled by design.** |
| `AgentExecutionContextFactory.cs:424-428` | `new AgentSkillsProviderBuilder().UseFileScriptRunner(...)`, `.UseFileSkill(path)` per resolved path, `.Build()` — added **first** in the provider list |
| `Infrastructure.AI/MetaHarness/AgentEvaluationService.cs:32, 233-236` | Second, independent builder for candidate-skill evaluation. Duplicates its own `NoOpScriptRunner`. |

XML docs in four files still name `FileAgentSkillsProvider`, a type that no longer exists.

### 3b. Our real SKILL.md files are incompatible with the framework parser

Verified against shipped files. `skills/research-agent/SKILL.md`:

```yaml
name: "research-agent"
description: "..."
category: "research"          # ← silently dropped by framework parser
skill_type: "analysis"        # ← silently dropped
version: "1.2.0"              # ← silently dropped
tags: ["research", ...]       # ← silently dropped
allowed-tools: ["file_system"]  # ← read, but as the literal string '["file_system"]'
tools:                        # ← silently dropped; nested list-of-objects
  - name: "file_system"
    operations: ["read", "search", "list"]
```

`plugins/workspace-skill/skills/workspace/SKILL.md` additionally carries `denied-tools`, `sandbox-required`, and a nested `egress.allowlist`.

**Even the one key the framework reads, it would read wrong** — we write a YAML list, it expects a space-delimited string.

**And the `metadata:` escape hatch cannot hold this data**: `tools` is a list of objects and `egress` is a nested map; the framework's flat string-only parser cannot represent either. This is not "awkward to migrate" — it is **not representable**.

### 3c. The custom surface that is genuinely load-bearing

`SkillDefinition` has 55 members; **23 are read in production**:

`Id, Name, Description, Instructions, Version, Category, SkillType, Tags, AllowedTools, ModelOverride, AgentId, Prerequisites, CompletionTool, BaseDirectory, LoadedAt, Metadata, Tools, ToolDeclarations, Egress, PluginSource, Mode, HasTags, HasPrerequisites`

Consumers that constrain any change:

| Consumer | Constraint |
|---|---|
| `Infrastructure.AI.MCPServer/Tools/SkillTools.cs` | **Published MCP wire shape** — `list_skills`, `get_skill`, `find_skills_by_tag`. Widest external contract; tests assert exact camelCase field names. |
| `Domain.AI/Bundles/EphemeralAgentOverlay.cs:28`, `StagedBundle.cs:52` | Hold `IReadOnlyList<SkillDefinition>` — **serialized bundle-API contract** |
| `Application.Core/Permissions/PluginPermissionRuleProvider.cs` | Reads `PluginSource`, `AllowedTools`, `ToolDeclarations` for governance |
| `Infrastructure.AI/Egress/SkillManifestEgressPolicyResolver.cs:115` | Reads `Egress.Allowlist` — network egress policy |

`SkillMetadataParser.cs` is **636 lines** (not the ~150 the old plan claimed) with 41 tests across 5 files. It exists precisely because the framework surfaces only name + description.

---

## 4. Conclusion on the original premise

**Do not migrate the skill model or parser onto the framework's types.** Doing so would:

1. Lose ~12 custom frontmatter fields per skill, silently
2. Lose runtime tool enforcement (`ToolPermissionFilter` is the only thing enforcing `allowed-tools`; the framework's equivalent field is never read) — **security-relevant**
3. Lose token budgeting (`IContextBudgetTracker` has no framework equivalent)
4. Break a published MCP wire contract and a serialized bundle-API contract
5. Force every SKILL.md to be rewritten, and still fail on the structured fields

**Instead: keep our layer, and extend the framework at `AgentSkillsSource` — the seam Microsoft's own MCP package uses.**

---

## 5. The real defect: progressive disclosure is switched off — FIXED (budget accounting still open)

> **Resolved 2026-08-02**, except for the budget-tracker item — see the Phase 1 note in §6, which is
> now *more* wrong than before this change, not less. Fixing the disclosure defect required repairing two
> further defects found on the way, because all three had to land together: disclosure is worthless if
> `load_skill` cannot be called, and enabling `load_skill` is unsafe while the control that governs
> framework-injected tools does nothing.
>
> 1. **Eager injection removed, conditionally.** `SkillInstructionMerger.Merge` now takes the set of
>    skills the framework provider will serve and omits their bodies. Coverage is decided by
>    `FrameworkSkillCoverage`, and every ambiguity resolves to "keep the body". A skill the provider will
>    not serve keeps eager injection and is logged at Debug, so the fallback is visible rather than silent.
>
>    That predicate **re-reads the SKILL.md frontmatter rather than trusting `SkillDefinition`**, and the
>    reason is a trap worth recording: `SkillMetadataParser.cs:51-52` defaults a missing `name` to the
>    *directory name* and a missing `description` to the empty string, so a parsed `SkillDefinition` cannot
>    distinguish "declared" from "defaulted". The framework requires both to be present and valid in the
>    file. A directory-name comparison against the parsed `Name` therefore passes *tautologically* for
>    exactly the malformed manifests the loader rejects — dropping their instructions silently. Field
>    validity is delegated to the framework's public `AgentSkillFrontmatter.ValidateName` /
>    `ValidateDescription` (kebab-case charset, 64/1024-char caps) instead of being mirrored here.
> 2. **`load_skill` was unreachable.** The framework wraps all three skill tools in
>    `ApprovalRequiredAIFunction` by default; a call then returns `ToolApprovalRequestContent` instead of
>    invoking the tool, and **no turn-driver in this harness answers that** (grep: zero production
>    handlers). On-demand disclosure could therefore never have completed. `SkillDisclosureDefaults`
>    disables the three approval flags at both builder sites.
> 3. **`ToolPermissionFilter` enforced nothing.** It overrode `ProvideAIContextAsync`, which is
>    contractually *additive* — `AIContextProvider.InvokingCoreAsync` returns
>    `input.Tools.Concat(provided.Tools)`, so every tool it "stripped" was merged straight back in. All 12
>    of its tests passed because each called the protected method directly, bypassing the merge the runtime
>    always applies. It now overrides `InvokingCoreAsync`, and its tests drive the public `InvokingAsync`.
>    `load_skill` and `read_skill_resource` are exempt from the allow-list (no skill manifest names them,
>    so filtering them would disable disclosure for exactly the agents that declare tool restrictions);
>    `run_skill_script` is **not** exempt.
>
> Tests: `FrameworkSkillCoverageTests` (18), `AgentExecutionContextFactoryProgressiveDisclosureTests` (4),
> rewritten `ToolPermissionFilterTests` (17). Every decision point was mutation-tested — including a
> mutation that restores the tautological name comparison, which four tests catch.
>
> **Two changes here exceed what §6 Phase 1 asked for**, both deliberate. (a) The `ToolPermissionFilter`
> repair: §7 listed that class under "what we deliberately keep", assuming it worked. (b)
> `DisableRunSkillScriptApproval` — disclosure needs only the two read-only tools, but because this harness
> has no approval channel at all, leaving *any* skill tool approval-gated means a model that calls it
> stalls the turn with an unanswerable request. The script runner is a no-op, so the flag protected
> nothing; `run_skill_script` remains subject to the allow-list, which is the control that does.
>
> The original analysis is kept below because it is the evidence for why the change was made.

**Severity: worth fixing. Confidence: verified in code.**

`CLAUDE.md` documents the harness's headline skills architecture as three-tier progressive disclosure — Tier 1 index card (~100 tokens) always loaded, Tier 2 full instructions on demand, Tier 3 resources only on execution.

The code does not do this.

In `AgentExecutionContextFactory.MapToAgentContextAsync`:

- **Line 110** — `SkillInstructionMerger.Merge(skills, options.AdditionalContext, options.AgentInstructions)` emits `skill.Instructions` **verbatim** into the static system prompt (`SkillInstructionMerger.cs:60-69`).
- **`skill.Instructions` is the entire SKILL.md body**, minus two named sections — `SkillMetadataParser.ExtractStructuredSections` (line 160-166) sets it to `StripSections(body, "Objectives", "Trace Format")`. It is not a summary.
- **Lines 424-428** simultaneously hand the same skill directories to `AgentSkillsProviderBuilder`, whose provider advertises each skill and offers `load_skill` — which returns *that same full body* on request.

### Three consequences

1. **Tier 2 is eagerly loaded every turn.** The documented ~100-token index card is not what ships; the full body of every active skill is in the system prompt on every request.
2. **Duplicate content is reachable.** If the model calls `load_skill` for a skill already baked into its prompt, it receives the body twice.
3. **Budget reporting is understated.** `IContextBudgetTracker` records the injected half (`AgentExecutionContextFactory.cs:135, 146`) but is blind to anything the model pulls via `load_skill` / `read_skill_resource`. Note also line 145 uses a hardcoded `tools.Count * 50` token estimate.

### Provider ordering is load-bearing

Documented in `BuildMergedAIContextProviders` and must be preserved by any change:

```
AgentSkillsProvider (428) → ToolPermissionFilter (435) → KnowledgeMemoryContextProvider (449)
  → LearningsRecallContextProvider (467) → GoverningToolContextProvider (482, LAST so it wraps the final filtered tool set)
```

---

## 6. Proposed work

Not yet scheduled. Each phase is independently valuable and independently revertible.

### Phase 1 — Restore progressive disclosure (the real fix) — ◐ PARTLY DONE 2026-08-02

See the resolution box in §5. The one item below that did **not** ship is the budget-tracker integration:
`IContextBudgetTracker` still records only the injected half, so with bodies now arriving via `load_skill`
the reported system-prompt total is *lower* than the tokens actually spent on a turn where the model loads
a skill. The numbers moved in the right direction but they are still not the truth. Open question 2 (the
integration point) remains unanswered — that is the next piece of work in this area.

**Goal:** stop eagerly injecting full skill bodies; let the framework provider do the job it is already wired to do.

- Change what `SkillInstructionMerger` contributes for skills the framework provider already covers — the index card (name + description), not the full body. Agent-level instructions and `AdditionalContext` are unaffected.
- Ensure `IContextBudgetTracker` observes tool-loaded content. Two candidate approaches: middleware on the tool-invocation pipeline, or a `DelegatingAgentSkillsSource` wrapper. **Approach not yet chosen — needs a spike.**
- Preserve the exact provider ordering in §5.

**Risk: medium.** Touches prompt composition. Covered by `AgentExecutionContextFactoryTests.cs` (46 tests), `AgentExecutionContextFactoryPromptComposerTests.cs` (7 tests — one pins the exact legacy instruction string and will need deliberate updating), `SkillInstructionMergerTests.cs` (4 tests).

#### ⚠️ Hard prerequisite: the `load_skill` fallback is conditional

**Verified 2026-08-01.** The framework provider is not always wired, so removing eager injection unconditionally can leave an agent with **no instructions at all, silently**.

- `AgentExecutionContextFactory.cs:422` wires the provider only `if (skillPaths.Count > 0)`.
- `ResolveSkillPaths` (line 498) skips any skill whose directory is missing: `if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;` (line 520), and filters configured roots by `Directory.Exists` (line 508).
- Therefore in-memory/synthesized skills, and any host whose `AI.Skills.AllPaths` do not resolve under `AppContext.BaseDirectory`, produce an empty path list → **no `AgentSkillsProvider`, no `load_skill` tool**.

In that state the eager merge at line 110 is the *only* thing supplying skill instructions.

**The fix must therefore be conditional on the provider actually being wired for a given skill, or must carry an explicit fallback.** A blanket removal is a silent-failure bug.

Secondary effect to expect: `SkillInstructionsSectionProvider` returns `null` on empty instructions (line 54-55), so the composer-on path degrades quietly to an identity-only prompt rather than erroring — a silent quality regression, not a crash. It also sets `IsRequired = true`, so budget pressure can never drop the section.

#### Resolved: `SkillMode` does **not** gate this

**Verified 2026-08-01 — the previously-open question is closed.** `SkillMode` affects **tool wiring only** and has no bearing on instructions:

- `SkillMode.cs` type-level doc: *"Determines how tools are resolved for a skill."* Both enum values' docs describe tool resolution exclusively. "Injected" means injected **tools**, not injected **instructions** — a naming trap.
- Exactly **one** production read site: `ToolChainBuilder.cs:54` — `if (skill.Mode == SkillMode.Injected && _mcpToolProvider != null)`. Both branches return `List<AITool>`. `AgentExecutionContextFactory` never reads `Mode` at all.
- `Mode` is a **get-only computed property** derived from tool fields (`SkillDefinition.cs:270-273`) — no setter, no backing field, not parseable from frontmatter. No shipped SKILL.md sets a mode; `grep '^mode:'` across all 20 returns nothing.
- `AgentExecutionContextFactoryDualModeTests.cs` (5 tests): all nine assertions are on `context.Tools`. None touches `context.Instructions`.

**Consequence for the fix: make it uniform, not mode-aware.** A mode-aware split would be the first instruction-affecting use of `Mode`, contradicting its own documentation, and because `Mode` is derived it would land backwards — preserving eager injection for all six harness-native skills while stripping it only from declaration-free *plugin* skills. It would also couple prompt size to tool configuration: adding an `allowed-tools:` line to a plugin SKILL.md would silently flip its instructions back into the system prompt.

### Phase 2 — Extend at the framework seam

**Goal:** replace ad-hoc wiring with a first-class custom source, matching Microsoft's own pattern.

- Implement `HarnessSkillsSource : AgentSkillsSource` returning our `SkillDefinition`-backed skills.
- Add a builder extension method, mirroring `UseMcpSkills`.
- Collapse the duplicate `NoOpScriptRunner` in `AgentEvaluationService.cs:32` onto one shared definition.

**Risk: low.** Additive. Note `AgentEvaluationServiceTests.cs:268,293` asserts `AIContextProviders.OfType<AgentSkillsProvider>().Single()`.

### Phase 3 — Cleanup (independent of 1 and 2; safe any time)

- **Delete `SkillChangedEventArgs.cs`** — verified dead: the only references are the type itself and its own test file. Delete both.
- **Reconcile the two `SkillSources` classes** — `Domain.AI.Constants.SkillSources` (4 constants, `FileSystem` casing) and `Domain.AI.Skills.SkillSources` (5 constants, `Filesystem` casing, adds `Inline`). Neither is used in production code; the exception's doc `cref` points at the `Constants` one while the usage example lives on the `Skills` one. Pick one, update the `cref`, delete the other.
- **Fix `Application.AI.Common/README.md`** — remove references to `ITieredContextAssembler` and `Services/Context/TieredContextAssembler.cs` (lines 127, 175, 287, 332, 333). Neither exists.
- **Fix stale XML docs** naming `FileAgentSkillsProvider` in `ISkillMetadataRegistry.cs:8,12`, `ToolPermissionFilter.cs:13`, `SkillMetadataRegistry.cs:10,22`.
- Consider the ~14 never-read `SkillDefinition` members. **Caveat:** `Objectives`/`TraceFormat` are written by the parser and asserted by `SkillParserExtensionTests.cs`; `Templates`/`References`/`Scripts`/`Assets` are `SkillResource`'s only consumers. Deleting these is a larger decision than it appears.

---

## 7. What we deliberately keep, and why

| Ours | Why the framework cannot replace it |
|---|---|
| `SkillMetadataParser` (636 lines) | Framework promotes 5 frontmatter keys and silently drops the rest; its `metadata:` block is flat-string-only and cannot hold `tools` or `egress` |
| `ISkillMetadataRegistry` | Framework has no query-by-category/tag/type; `AgentSkillsSource` returns a flat list and the provider renders only name + description |
| `ToolPermissionFilter` | Framework's `AllowedTools` is **never read** — this is the only runtime enforcement for framework-injected tools. It did not actually enforce anything until 2026-08-02 (§5); it must override `InvokingCoreAsync`, never `ProvideAIContextAsync`. No longer subclassed by its tests, which now drive the public `InvokingAsync`, so nothing depends on it staying unsealed |
| `IContextBudgetTracker` | Framework has zero token accounting |
| `SkillDefinition` | Backs a published MCP wire shape and a serialized bundle-API contract |

---

## 8. Relationship to issue #219

Issue #219 tracks Microsoft's MCP-based skill discovery (`Microsoft.Agents.AI.Mcp`, currently pre-release and flagged experimental).

**It plugs into the same `AgentSkillsSource` seam as Phase 2** — `UseMcpSkills` is a one-line extension method calling `UseSource(...)`. Completing Phase 2 therefore makes #219 close to a drop-in addition when the package stabilizes.

Note for that work: the MCP `skill-md` path constructs `new AgentSkillFrontmatter(name, description)` with **no metadata at all**, so MCP-sourced skills would arrive with none of our custom fields. Archive-type entries are extracted and passed through `AgentFileSkillsSource`, so they *do* get the `metadata:` block treatment. Any adoption must decide how MCP-sourced skills participate in governance, egress policy, and tool enforcement — they cannot today.

---

## 9. Open questions

1. ~~**`SkillMode` semantics** — does `Injected` mode require eager full-body injection?~~ **CLOSED 2026-08-01: no.** Mode is tool-wiring only and is a derived get-only property. The fix must be uniform, not mode-aware. Evidence in §6 Phase 1. Do not re-derive.
2. **Budget-tracker integration point** — middleware on tool invocation, or a `DelegatingAgentSkillsSource` wrapper? Needs a spike.
3. **`SkillMetadataParser.Parse(skillName, skillDescription, body, ...)`** (line 81) — a hybrid overload built to accept pre-parsed framework input. It has **no production caller** (tests only). It appears to be the intended seam for exactly this integration; confirm before Phase 2 whether to use or delete it.
4. **`FrameworkLoaderSpikeTests.cs`** hard-codes `src/Content/Application/Application.Core/Agents/Skills` and only asserts construction succeeds. If kept, it should assert actual provider output.
