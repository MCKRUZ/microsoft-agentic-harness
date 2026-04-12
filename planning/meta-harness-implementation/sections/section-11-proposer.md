# Section 11: Proposer (`IHarnessProposer` + `OrchestratedHarnessProposer`)

## Overview

This section implements the proposer component of the meta-harness optimization loop. The proposer is an orchestrated agent that reads execution traces from prior candidates and proposes an improved harness configuration (skill file changes, system prompt changes, config changes). It delegates to the existing `RunOrchestratedTaskCommandHandler` pipeline with a restricted, trace-directory-scoped tool set.

This section is parallelizable with `section-12-evaluator` and `section-13-tools`. It blocks `section-14-outer-loop`.

## Dependencies (must be complete before starting)

| Section | What it provides |
|---|---|
| section-04-trace-infrastructure | `IExecutionTraceStore`, `ITraceWriter`, `TraceScope` — the proposer reads the trace directory |
| section-06-history-store | `IAgentHistoryStore`, `ReadHistoryTool` — included in proposer tool set |
| section-07-skill-extension | Updated `SkillSection` types (`Objectives`, `TraceFormat`) — `skills/harness-proposer/SKILL.md` uses them |
| section-08-skill-provider | `ISkillContentProvider` — wired into `AgentExecutionContextFactory` |
| section-09-candidate-domain | `HarnessCandidate`, `HarnessProposal`, `HarnessProposerContext` domain types |
| section-10-candidate-repository | `IHarnessCandidateRepository` — proposer context includes prior candidate IDs for trace navigation |

## New Files to Create

```
Application.AI.Common/
  Interfaces/MetaHarness/IHarnessProposer.cs

Infrastructure.AI/
  MetaHarness/OrchestratedHarnessProposer.cs

skills/harness-proposer/
  SKILL.md

Tests/Infrastructure.AI.Tests/
  MetaHarness/OrchestratedHarnessProposerTests.cs
```

## Tests First

**Test project:** `Tests/Infrastructure.AI.Tests/MetaHarness/OrchestratedHarnessProposerTests.cs`

Framework: xUnit + Moq. Arrange-Act-Assert. Test naming: `MethodName_Scenario_ExpectedResult`.

### Test stubs

```csharp
/// <summary>
/// Tests for OrchestratedHarnessProposer JSON extraction and error handling.
/// Uses a mock IMediator that returns scripted agent output strings.
/// </summary>
public class OrchestratedHarnessProposerTests
{
    /// <summary>
    /// When the agent returns a string containing a valid JSON block, ProposeAsync
    /// should extract the first '{' to last '}' substring, parse it, and return a
    /// populated HarnessProposal.
    /// </summary>
    [Fact]
    public async Task ProposeAsync_ValidJsonBlock_ReturnsParsedProposal() { }

    /// <summary>
    /// When the agent returns text that contains no valid JSON object (no matching
    /// braces), ProposeAsync should throw HarnessProposalParsingException with the
    /// raw output included in the exception message.
    /// </summary>
    [Fact]
    public async Task ProposeAsync_InvalidJsonOutput_ThrowsHarnessProposalParsingException() { }

    /// <summary>
    /// When the JSON block is valid but ProposedSkillChanges and ProposedConfigChanges
    /// are absent or empty, ProposeAsync should return a HarnessProposal with empty
    /// dictionaries (not null) and a non-null Reasoning string.
    /// </summary>
    [Fact]
    public async Task ProposeAsync_EmptyProposedChanges_ReturnsProposalWithEmptyDicts() { }

    /// <summary>
    /// When the JSON block includes a "reasoning" field, its value should be surfaced
    /// on HarnessProposal.Reasoning verbatim.
    /// </summary>
    [Fact]
    public async Task ProposeAsync_ProposalContainsReasoning_ReasoningPassedThrough() { }
}
```

### What the tests must cover

- JSON extraction logic: the proposer scans raw agent output for the first `{` and last `}` character positions and feeds that substring to `JsonDocument.Parse`. Any preamble or postamble text (e.g. "Here is my proposal:\n{...}\n\nLet me know...") must be stripped correctly.
- `HarnessProposalParsingException` is a custom exception type (see below); tests verify it is thrown (not swallowed) so the outer loop can catch it and mark the candidate `Failed`.
- Empty-changes case: the proposer must not throw when a proposal has no skill/config/prompt changes — this is a valid (if unhelpful) proposal.
- Reasoning passthrough: the `Reasoning` field on `HarnessProposal` is populated from the JSON `"reasoning"` key.

## Interface Definition

**File:** `src/Content/Application/Application.AI.Common/Interfaces/MetaHarness/IHarnessProposer.cs`

```csharp
/// <summary>
/// Proposes an improved harness configuration by running an orchestrated agent
/// that reads execution traces from prior candidates.
/// </summary>
public interface IHarnessProposer
{
    /// <summary>
    /// Analyzes prior execution traces and returns a proposed harness change set.
    /// </summary>
    /// <exception cref="HarnessProposalParsingException">
    /// Thrown when the agent's output cannot be parsed as a valid JSON proposal.
    /// </exception>
    Task<HarnessProposal> ProposeAsync(HarnessProposerContext context, CancellationToken cancellationToken);
}
```

## Value Objects

Both `HarnessProposerContext` and `HarnessProposal` are immutable records. Place them in `Domain.Common/MetaHarness/` alongside `HarnessCandidate`.

**`HarnessProposerContext`** — input to the proposer:

| Property | Type | Description |
|---|---|---|
| `CurrentCandidate` | `HarnessCandidate` | The candidate to improve upon |
| `OptimizationRunDirectoryPath` | string | Absolute path to `optimizations/{optRunId}/`; proposer's filesystem sandbox root |
| `PriorCandidateIds` | `IReadOnlyList<Guid>` | All prior candidate IDs in this run (oldest first); for trace navigation |
| `Iteration` | int | Current iteration number (1-based) |

**`HarnessProposal`** — output from the proposer:

| Property | Type | Description |
|---|---|---|
| `ProposedSkillChanges` | `IReadOnlyDictionary<string, string>` | Skill file path → new content; empty dict if no changes |
| `ProposedConfigChanges` | `IReadOnlyDictionary<string, string>` | Config key → new value; empty dict if no changes |
| `ProposedSystemPromptChange` | string? | Replacement system prompt; null if no change |
| `Reasoning` | string | Agent's explanation of why these changes were proposed |

## Exception Type

**File:** `src/Content/Application/Application.AI.Common/Exceptions/HarnessProposalParsingException.cs`

```csharp
/// <summary>
/// Thrown by <see cref="IHarnessProposer"/> when the agent's output cannot be
/// parsed as a valid JSON proposal block. The outer loop catches this to mark the
/// candidate as <see cref="HarnessCandidateStatus.Failed"/> and continue.
/// </summary>
public sealed class HarnessProposalParsingException : Exception
{
    /// <summary>Gets the raw agent output that failed to parse.</summary>
    public string RawOutput { get; }

    public HarnessProposalParsingException(string rawOutput, string? message = null, Exception? inner = null)
        : base(message ?? $"Failed to parse harness proposal from agent output. Raw output length: {rawOutput.Length}", inner)
        => RawOutput = rawOutput;
}
```

## Implementation: `OrchestratedHarnessProposer`

**File:** `src/Content/Infrastructure/Infrastructure.AI/MetaHarness/OrchestratedHarnessProposer.cs`

### Constructor dependencies

```csharp
public OrchestratedHarnessProposer(
    IMediator mediator,
    IOptionsMonitor<MetaHarnessConfig> config,
    ILogger<OrchestratedHarnessProposer> logger)
```

### `ProposeAsync` implementation outline

1. **Build the task prompt** — include `context.OptimizationRunDirectoryPath`, iteration number, and a pointer to the prior candidates' eval subdirectories. The prompt must instruct the agent to respond with a JSON object only (no markdown fences) containing keys: `reasoning`, `proposed_skill_changes`, `proposed_config_changes`, `proposed_system_prompt_change`.

2. **Assemble the tool set** — always include `FileSystemService` (sandboxed to `context.OptimizationRunDirectoryPath`) and `ReadHistoryTool`. Add `RestrictedSearchTool` only when `config.CurrentValue.EnableShellTool == true`. Add MCP trace resources only when `config.CurrentValue.EnableMcpTraceResources == true`.

3. **Dispatch via `IMediator`** — send a `RunOrchestratedTaskCommand` (existing command) with the proposer's system prompt loaded from `skills/harness-proposer/SKILL.md` and the assembled tool set.

4. **Extract JSON block** — from the raw string output:
   ```csharp
   var start = rawOutput.IndexOf('{');
   var end = rawOutput.LastIndexOf('}');
   if (start < 0 || end <= start)
       throw new HarnessProposalParsingException(rawOutput);
   var json = rawOutput[start..(end + 1)];
   ```

5. **Parse with `JsonDocument`** — wrap in `try/catch JsonException`; on failure throw `HarnessProposalParsingException(rawOutput, inner: ex)`.

6. **Map to `HarnessProposal`** — read each expected JSON key; use empty dictionaries when keys are absent; never return null for dictionary properties.

7. **Log the proposal** — log at `Information` level: iteration number, number of skill changes, number of config changes, whether a system prompt change is present. Never log the full content (may contain secrets).

### JSON schema the proposer agent must emit

```json
{
  "reasoning": "Explanation of the proposed changes and why they should improve performance.",
  "proposed_skill_changes": {
    "skills/harness-proposer/SKILL.md": "Full replacement content for this skill file"
  },
  "proposed_config_changes": {
    "MetaHarness:EvaluationTemperature": "0.2"
  },
  "proposed_system_prompt_change": null
}
```

All keys are optional except `"reasoning"`. Missing dictionary keys map to empty `IReadOnlyDictionary<string, string>`. A `null` or absent `"proposed_system_prompt_change"` maps to `null` on the record.

## Skill File: `skills/harness-proposer/SKILL.md`

**File:** `skills/harness-proposer/SKILL.md`

This skill file uses the extended `## Objectives` and `## Trace Format` sections added in section-07. It is itself part of the harness and therefore a valid target for the proposer to modify.

Minimum required sections (use the extended SKILL.md format established in section-07):

```markdown
# harness-proposer

## Role
You are a harness engineer. Your job is to analyze execution traces from prior agent
runs and propose targeted improvements to the harness configuration.

## Objectives
- Improve pass rate on the eval task set
- Prefer minimal, targeted changes over broad rewrites
- Explain your reasoning before proposing changes
- Identify patterns in trace failures (timeouts, wrong tool selection, hallucinations)
- Do not change config values you have no evidence to change

## Trace Format
The optimization run directory contains:
  candidates/{candidateId}/eval/{taskId}/{executionRunId}/traces.jsonl  — per-turn tool call trace
  candidates/{candidateId}/eval/{taskId}/{executionRunId}/decisions.jsonl  — agent decision log
  candidates/{candidateId}/candidate.json  — the full harness snapshot for that candidate
  candidates/index.jsonl  — summary: {candidateId, passRate, tokenCost, status, iteration}
  run_manifest.json  — {lastCompletedIteration, bestCandidateId, write_completed}

Use `read_history` or filesystem tools to read these files before proposing changes.

## Output Format
Respond with a single JSON object (no markdown fences, no preamble):
{
  "reasoning": "...",
  "proposed_skill_changes": { "<skill_path>": "<full_content>" },
  "proposed_config_changes": { "<config_key>": "<value>" },
  "proposed_system_prompt_change": null
}
```

## DI Registration

**File to modify:** `src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs`

Add to the existing `AddInfrastructureAIDependencies()` extension method:

```csharp
services.AddScoped<IHarnessProposer, OrchestratedHarnessProposer>();
```

Use `AddScoped` (not singleton) because each proposer invocation carries iteration-specific context.

## File Path Summary

| Action | Path |
|---|---|
| Create | `src/Content/Application/Application.AI.Common/Interfaces/MetaHarness/IHarnessProposer.cs` |
| Create | `src/Content/Application/Application.AI.Common/Exceptions/HarnessProposalParsingException.cs` |
| Create | `src/Content/Domain/Domain.Common/MetaHarness/HarnessProposerContext.cs` |
| Create | `src/Content/Domain/Domain.Common/MetaHarness/HarnessProposal.cs` |
| Create | `src/Content/Infrastructure/Infrastructure.AI/MetaHarness/OrchestratedHarnessProposer.cs` |
| Create | `skills/harness-proposer/SKILL.md` |
| Create | `src/Content/Tests/Infrastructure.AI.Tests/MetaHarness/OrchestratedHarnessProposerTests.cs` |
| Modify | `src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs` |
| Modify | `src/Content/Infrastructure/Infrastructure.AI/Infrastructure.AI.csproj` |

## Implementation Notes

**Deviations from plan:**

- `HarnessProposalParsingException` extends `ApplicationExceptionBase` (not raw `Exception`) — matches project convention used by all other exceptions in `Application.AI.Common/Exceptions/`.
- `HarnessProposalParsingException.RawOutput` is truncated to 500 chars — prevents full agent output from leaking into structured logging on exception serialization.
- `HarnessProposerContext.Iteration` doc corrected to **zero-based** (spec said 1-based, but `HarnessCandidate.Iteration` is zero-based — standardized to match).
- `Infrastructure.AI.csproj` required a new `ProjectReference` to `Application.Core` — `RunOrchestratedTaskCommand` lives there and was not previously referenced by the infrastructure layer.
- `skills/harness-proposer/SKILL.md` updated to v2.0 (file already existed at v1.0 with markdown proposal format; updated to structured JSON output format required by this section).
- `AvailableAgents` on `RunOrchestratedTaskCommand` is populated with tool names (`"file_system"`, `"read_history"`) rather than sub-agent names — noted mismatch; left as-is pending section-14 outer loop wiring.
- Test file uses `$$$"""..."""` raw string interpolation (triple `$`) to avoid `CS9007` — `$$"""` cannot contain `}}` as literal content.

**Tests:** 4 tests, 4 passed (`Infrastructure.AI.Tests.dll`).
