# Section 15: Console UI — `optimize` Command

## Overview

This section adds the `optimize` command to `Presentation.ConsoleUI/`. It is the final section in the implementation sequence and depends entirely on section 14 (`RunHarnessOptimizationCommand` + handler being complete and registered).

**Dependency:** section-14-outer-loop must be complete before implementing this section.

**No automated tests.** Console I/O is verified manually. The TDD plan explicitly marks this section as "manual test only" (step 17 in the execution order).

---

## Implementation Notes (Actual vs. Planned)

- **No `Description` field**: The actual `RunHarnessOptimizationCommand` (section 14) has no `Description` or `OnIterationComplete` property. The UI uses `AnsiConsole.Status()` spinner instead of per-iteration callbacks.
- **`OptimizationRunId` is `Guid`**: Not `string` as the spec stated. Generated via `Guid.NewGuid()`.
- **Result type is `OptimizationResult`**: Not `Result<OptimizationRunResult>`. Fields: `BestScore`, `IterationCount`, `ProposedChangesPath`.
- **Review fixes applied**: `Markup.Escape()` on `ProposedChangesPath` (W1), invalid-input feedback for max iterations (W2), `--example optimize` added to `Program.cs` XML docs (S1).

## What to Build

### 1. New example class

**File:** `src/Content/Presentation/Presentation.ConsoleUI/Examples/OptimizeExample.cs`

This follows the exact pattern of the existing example classes (`ResearchAgentExample`, `OrchestratorExample`, etc.):

- Constructor-injected `ISender` (MediatR) and `ILogger<OptimizeExample>`
- Public `RunAsync(CancellationToken)` method
- Uses `Spectre.Console` for all user interaction and progress display

The `RunAsync` flow:

1. Display a header via `ConsoleHelper.DisplayHeader("Meta-Harness Optimizer", Color.Gold1)`
2. Prompt the user for a run description (non-empty string, re-prompt on blank)
3. Prompt the user for an optional max iterations override — show the config default in the prompt text, accept empty input to use the default (pass `null` to the command so the handler respects `MetaHarnessConfig.MaxIterations`)
4. Generate an `OptimizationRunId` as `Guid.NewGuid().ToString("N")`
5. Dispatch `RunHarnessOptimizationCommand` via `_sender.Send(...)` with a `Progress` callback for per-iteration output
6. On completion, print the path to `_proposed/` and usage instructions

**Per-iteration progress line format** (output as each iteration completes via the progress callback):

```
Iteration {i}/{max} | Score: {score:P1} | Δ{delta:+P1} | Tokens: {tokens:N0} | {changeSummary}
```

Where `delta` is the score improvement over the previous iteration (or `+0.0%` for the first iteration).

**Completion output:**

```
Optimization complete. Best candidate written to:
  {proposedPath}

To review: inspect _proposed/ for modified skill files and system prompt.
To promote: copy _proposed/ contents over the live skills/ directory.
```

Use `AnsiConsole.MarkupLine` with `[bold green]` for the "complete" line and `[grey]` for the path.

### 2. Wire into `App.cs`

**File:** `src/Content/Presentation/Presentation.ConsoleUI/App.cs`

Add `OptimizeExample` as a constructor parameter and backing field, following the exact same pattern as every other example. Add it to:

- The `[bold]Agents[/]` choice group in `MainMenuAsync()` — label: `"Meta-Harness Optimizer"`
- The `switch` in `MainMenuAsync()` — route `"Meta-Harness Optimizer"` to `await _optimizeExample.RunAsync()`
- The `switch` in `RunExampleAsync()` — route `"optimize"` to `await _optimizeExample.RunAsync()`

### 3. Register in `Program.cs`

**File:** `src/Content/Presentation/Presentation.ConsoleUI/Program.cs`

Add `services.AddTransient<OptimizeExample>();` alongside the other example registrations.

---

## The Command Contract

`RunHarnessOptimizationCommand` (defined in section 14, file `src/Content/Application/Application.Core/CQRS/MetaHarness/RunHarnessOptimizationCommand.cs`) is expected to have this shape — do not redefine it, just consume it:

```csharp
public sealed record RunHarnessOptimizationCommand : IRequest<Result<OptimizationRunResult>>
{
    public required string OptimizationRunId { get; init; }
    public required string Description { get; init; }
    public int? MaxIterationsOverride { get; init; }
    public Action<IterationProgress>? OnIterationComplete { get; init; }
}
```

`IterationProgress` (also from section 14):

```csharp
public sealed record IterationProgress
{
    public required int IterationNumber { get; init; }
    public required int MaxIterations { get; init; }
    public required double Score { get; init; }
    public required double Delta { get; init; }
    public required long TotalTokens { get; init; }
    public required string ChangeSummary { get; init; }
}
```

`OptimizationRunResult` (also from section 14):

```csharp
public sealed record OptimizationRunResult
{
    public required string ProposedPath { get; init; }
    public required int CompletedIterations { get; init; }
    public required double BestScore { get; init; }
}
```

If these shapes differ from what section 14 actually produces, adjust the UI code to match — the command is the source of truth.

---

## Stub Signature

```csharp
namespace Presentation.ConsoleUI.Examples;

/// <summary>
/// Interactive example that runs the meta-harness optimization loop.
/// Prompts the user for a run description and optional iteration override,
/// dispatches <see cref="RunHarnessOptimizationCommand"/> via MediatR,
/// and streams per-iteration progress to the console.
/// </summary>
public class OptimizeExample
{
    public OptimizeExample(ISender sender, ILogger<OptimizeExample> logger) { }

    /// <summary>
    /// Runs the interactive optimization session.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default) { }
}
```

---

## Manual Verification Steps

Since there are no automated tests, verify this works by running:

```
dotnet run --project src/Content/Presentation/Presentation.ConsoleUI
```

Then:

1. Select "Meta-Harness Optimizer" from the menu (or run `--example optimize`)
2. Enter a description when prompted
3. Press Enter to use the default iteration count
4. Confirm per-iteration lines print with the expected format as the loop progresses
5. Confirm the completion message prints the `_proposed/` path
6. Verify the path exists on disk and contains the proposed skill files

Also confirm the build passes cleanly:

```
dotnet build src/AgenticHarness.slnx
```

---

## Dependencies Checklist

Before starting this section, confirm these are complete:

- `RunHarnessOptimizationCommand` exists in `Application.Core/CQRS/MetaHarness/`
- `RunHarnessOptimizationCommandHandler` is registered via MediatR assembly scanning
- `IterationProgress` and `OptimizationRunResult` types are defined and exported
- `MetaHarnessConfig` is bound and available via `IOptionsMonitor<AppConfig>` (section 01)
- All infrastructure services (`IHarnessProposer`, `IEvaluationService`, `IHarnessCandidateRepository`) are registered in their respective `DependencyInjection.cs` files
