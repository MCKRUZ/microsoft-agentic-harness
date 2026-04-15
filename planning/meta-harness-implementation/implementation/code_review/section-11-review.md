# Code Review: Section-11 Harness Proposer

## Summary

Two domain records (HarnessProposerContext, HarnessProposal), one Application interface (IHarnessProposer), one custom exception (HarnessProposalParsingException), one Infrastructure implementation (OrchestratedHarnessProposer, 149 lines), four tests, a SKILL.md update to v2.0, and a DI registration. The proposer dispatches a `RunOrchestratedTaskCommand` via MediatR, then extracts JSON from the agent `FinalSynthesis` string using first-brace-to-last-brace extraction.

Clean implementation overall. The primary concerns are: (1) an off-by-one inconsistency between `HarnessCandidate.Iteration` (0-based) and `HarnessProposerContext.Iteration` (1-based), (2) the first-brace/last-brace JSON extraction is fragile against nested JSON in agent reasoning text, (3) `RawOutput` on the exception may log sensitive trace data, and (4) missing input validation on context properties.

## Verdict: WARNING -- merge with fix for H-1; remaining items are low-risk improvements

---

## Detailed Findings

### H-1 | HIGH | Iteration indexing inconsistency -- 0-based vs 1-based

**Files:**
- `Domain.Common/MetaHarness/HarnessCandidate.cs:20-21` -- documented as "Zero-based iteration index"
- `Domain.Common/MetaHarness/HarnessProposerContext.cs:27` -- documented as "Current iteration number (1-based)."

**Problem:**
`HarnessCandidate.Iteration` is documented (and presumably used) as zero-based. `HarnessProposerContext.Iteration` is documented as 1-based. The test fixture sets `HarnessCandidate.Iteration = 0` and `HarnessProposerContext.Iteration = 1`, which looks intentional but creates a semantic trap: the outer loop must remember to convert between the two conventions, and any code that passes `candidate.Iteration` directly to the context will be off by one.

The `BuildTaskPrompt` method embeds `context.Iteration` directly in the agent prompt. If the outer loop forgets to add 1, the agent will see "iteration 0" which contradicts the "1-based" contract.

**Recommended fix:**
Standardize on one convention across the entire MetaHarness domain. Zero-based is the C# default and matches `HarnessCandidate`. Change `HarnessProposerContext.Iteration` to zero-based, or add a computed `DisplayIteration` property for the prompt:

```csharp
// Option A: Make context zero-based (preferred -- matches HarnessCandidate)
/// <summary>Zero-based iteration index within the optimization run.</summary>
public required int Iteration { get; init; }

// In BuildTaskPrompt, display as 1-based for the agent:
// Current iteration: {context.Iteration + 1}
```

**Blocker:** Yes. Off-by-one bugs in an optimization loop can cause iteration limit miscalculations, wrong candidate lookups, and confusing agent prompts.

---

### M-1 | MEDIUM | First-brace/last-brace JSON extraction is fragile

**File:** `Infrastructure.AI/MetaHarness/OrchestratedHarnessProposer.cs:103-108`

```csharp
var start = rawOutput.IndexOf('{');
var end = rawOutput.LastIndexOf('}');
```

**Problem:**
This extracts from the first open-brace to the last close-brace in the entire output string. If the agent preamble text or reasoning contains a JSON snippet, code example, or even a stray brace pair, the extraction window expands to include non-proposal content, producing either a `JsonException` or -- worse -- silently parsing the wrong object.

Example: if the agent says `Here is my analysis of {"Region": "eastus"}:` followed by the actual JSON proposal, the extraction spans from the first brace in the preamble through the last brace of the proposal -- invalid JSON.

The SKILL.md instructs the agent "no markdown fences, no preamble text," but agents routinely ignore output format instructions, especially on harder tasks.

**Recommended fix:**
Search backward from the last close-brace to find its matching open-brace by counting brace depth. This extracts the last complete JSON object rather than spanning across unrelated brace pairs:

```csharp
private static (int Start, int End) FindOutermostJsonObject(string text)
{
    var end = text.LastIndexOf('}');
    if (end < 0) return (-1, -1);
    int depth = 0;
    for (int i = end; i >= 0; i--)
    {
        if (text[i] == '}') depth++;
        else if (text[i] == '{') depth--;
        if (depth == 0) return (i, end);
    }
    return (-1, -1);
}
```

**Blocker:** No, but should fix. The SKILL.md mitigation works for happy paths, but agents are unpredictable.

---

### M-2 | MEDIUM | RawOutput on exception may contain sensitive trace data

**File:** `Application.AI.Common/Exceptions/HarnessProposalParsingException.cs:16`

```csharp
public string RawOutput { get; }
```

**Problem:**
The agent `FinalSynthesis` may echo back parts of the execution traces it analyzed, which could include file paths, config values, or other operational details. Storing the full raw output on the exception object means it will appear in any structured logging or error reporting pipeline that serializes exception properties.

The default message only logs the length, which is safe. But any logger that serializes the full exception (Serilog destructuring, Application Insights exception telemetry) will capture `RawOutput` in full.

**Recommended fix:**
Truncate `RawOutput` to a reasonable diagnostic length (e.g., first 500 characters):

```csharp
public string RawOutput { get; } = rawOutput.Length > 500
    ? rawOutput[..500] + "... [truncated]"
    : rawOutput;
```

Or keep the full output but mark it with `[JsonIgnore]` to prevent automatic serialization.

**Blocker:** No, but worth addressing for defense-in-depth.

---

### L-1 | LOW | No input validation on HarnessProposerContext

**File:** `Infrastructure.AI/MetaHarness/OrchestratedHarnessProposer.cs:51-53`

**Problem:**
`ProposeAsync` does not validate the incoming context. A null or empty `OptimizationRunDirectoryPath`, a negative `Iteration`, or a null `CurrentCandidate` would produce a confusing `NullReferenceException` or an invalid agent prompt rather than a clear validation error.

Per project conventions, validation should happen at system boundaries. A guard clause here would catch programming errors early.

**Recommended fix:**
Add `ArgumentNullException.ThrowIfNull` / `ArgumentException.ThrowIfNullOrWhiteSpace` guards at the top of `ProposeAsync`:

```csharp
ArgumentNullException.ThrowIfNull(context);
ArgumentNullException.ThrowIfNull(context.CurrentCandidate);
ArgumentException.ThrowIfNullOrWhiteSpace(context.OptimizationRunDirectoryPath);
```

**Blocker:** No.

---

### L-2 | LOW | BuildAgentList returns tool names, not agent names

**File:** `Infrastructure.AI/MetaHarness/OrchestratedHarnessProposer.cs:93-100`

**Problem:**
The method is named `BuildAgentList` and maps to `RunOrchestratedTaskCommand.AvailableAgents`, but the values (`"file_system"`, `"read_history"`, `"restricted_search"`) are tool names, not agent names. The `RunOrchestratedTaskCommand` property is documented as "Names of sub-agents available for delegation," which suggests these should be orchestrated agent identifiers, not tool keys.

This may work correctly if `RunOrchestratedTaskCommand` treats `AvailableAgents` as tool-or-agent identifiers interchangeably, but the naming mismatch is confusing.

**Recommended fix:**
Verify the handler behavior. If `AvailableAgents` does accept tool names, rename the method to `BuildToolList` for clarity. If it only accepts agent names, this is a bug.

**Blocker:** No, but warrants verification.

---

### L-3 | LOW | Test coverage gaps

**File:** `Infrastructure.AI.Tests/MetaHarness/OrchestratedHarnessProposerTests.cs`

**Missing test cases:**

1. **Malformed JSON (valid braces, invalid JSON):** e.g., `{not: valid json}` -- verifies that `JsonException` is wrapped in `HarnessProposalParsingException` rather than thrown raw.
2. **Non-string values in dictionaries silently dropped:** The `Where` filter in `ReadStringDict` silently drops integer/boolean/null values. A test should verify this behavior is intentional.
3. **System prompt change is a non-null string:** Current tests only cover null system prompt. Should test the non-null path.
4. **Cancellation:** No test verifies that `CancellationToken` is forwarded to the mediator.

**Blocker:** No. The existing 4 tests cover the critical paths well.

---

## Architecture and Convention Compliance

| Check | Status | Notes |
|-------|--------|-------|
| Clean Architecture layers | PASS | Domain records in Domain.Common, interface in Application.AI.Common, impl in Infrastructure.AI |
| Immutability | PASS | All records with required init properties, IReadOnlyDictionary/IReadOnlyList on public surfaces |
| Exception hierarchy | PASS | HarnessProposalParsingException extends ApplicationExceptionBase correctly |
| DI registration | PASS | AddScoped is appropriate -- each propose invocation carries iteration-specific state |
| File sizes | PASS | OrchestratedHarnessProposer is 149 lines, all others well under limits |
| Test naming | PASS | Follows MethodName_Scenario_ExpectedResult convention |
| No hardcoded secrets | PASS | No API keys, connection strings, or tokens |
| No console.log / Console.Write | PASS | Uses ILogger throughout |
