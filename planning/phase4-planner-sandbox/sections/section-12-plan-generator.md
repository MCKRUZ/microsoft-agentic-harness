# Section 12: Plan Generator

## Overview

This section implements `LlmPlanGeneratorService`, the `IPlanGenerator` implementation that converts a natural-language task description into a validated `PlanGraph` DAG. The service sends the task description plus a JSON schema describing the `PlanGraph` structure to an LLM, deserializes the structured JSON output, validates it via `IPlanValidator`, and returns the resulting graph.

**Depends on:**
- Section 01 (Domain Models) -- `PlanGraph`, `PlanStep`, `PlanEdge`, `StepType`, `StepConfiguration` subtypes, `PlanConfiguration`, `RetryPolicy`, `PlanId`, `PlanStepId`
- Section 02 (Application Interfaces) -- `IPlanGenerator`, `IPlanValidator`
- Section 03 (Plan Validation) -- `PlanValidator` must be available for the generator to validate before returning

**Blocks:** Section 13 (CQRS Commands) -- `GeneratePlanCommandHandler` delegates to `IPlanGenerator`.

**Parallelizable with:** Section 11 (Plan Executor) -- these two sections are independent.

---

## Tests

### File: `src/Content/Tests/Infrastructure.AI.Tests/Planner/LlmPlanGeneratorServiceTests.cs`

```csharp
// Test: GenerateAsync_ValidTask_ReturnsPlanGraph
//   Arrange: Mock IChatClient to return valid PlanGraph JSON with 3 steps and 2 edges.
//            Mock IPlanValidator to return success.
//   Act:    Call GenerateAsync("Build a REST API").
//   Assert: Result.IsSuccess is true. Result.Value is a PlanGraph with 3 steps and 2 edges.
//           PlanGraph.Id is non-empty. All steps have valid StepType values.

// Test: GenerateAsync_LlmOutput_ValidatedBeforeReturn
//   Arrange: Mock IChatClient to return valid PlanGraph JSON.
//            Mock IPlanValidator to return success.
//   Act:    Call GenerateAsync("any task").
//   Assert: Verify IPlanValidator.ValidateAsync was called exactly once with the deserialized PlanGraph.

// Test: GenerateAsync_InvalidLlmOutput_ReturnsFail
//   Arrange: Mock IChatClient to return malformed JSON (missing required fields).
//   Act:    Call GenerateAsync("any task").
//   Assert: Result.IsSuccess is false. Result.Errors contains deserialization failure message.
//           IPlanValidator.ValidateAsync was NOT called.

// Test: GenerateAsync_OutputPassesAllValidationChecks
//   Arrange: Mock IChatClient to return valid JSON with a cycle (A->B->A).
//            Mock IPlanValidator to return Fail with cycle detection error.
//   Act:    Call GenerateAsync("any task").
//   Assert: Result.IsSuccess is false. Result.Errors contain the validation failure.

// Test: GenerateAsync_WithConstraints_IncludesConstraintsInPrompt
//   Arrange: Mock IChatClient (capture the ChatMessage list).
//            Provide PlanGenerationConstraints with MaxSteps=5, AllowedStepTypes=[LlmCall, ToolUse].
//   Act:    Call GenerateAsync("Build API", constraints).
//   Assert: System/user message sent to LLM contains the constraint values.

// Test: GenerateAsync_LlmReturnsEmptyResponse_ReturnsFail
//   Arrange: Mock IChatClient to return empty/null content.
//   Act:    Call GenerateAsync("any task").
//   Assert: Result.IsSuccess is false. Errors describe empty LLM response.

// Test: GenerateAsync_CancellationRequested_ThrowsOrReturnsFail
//   Arrange: Mock IChatClient. Create a pre-cancelled CancellationToken.
//   Act:    Call GenerateAsync("any task", ct: cancelledToken).
//   Assert: OperationCanceledException is thrown.

// Test: GenerateAsync_AssignsPlanIdAndStepIds
//   Arrange: Mock IChatClient to return valid JSON where IDs are placeholders.
//   Act:    Call GenerateAsync("any task").
//   Assert: PlanGraph.Id is valid non-empty PlanId.
//           Each Step.Id is unique and non-empty. Edge From/To reference valid step IDs.
```

---

## Domain Type: PlanGenerationConstraints

### File: `src/Content/Domain/Domain.AI/Planner/PlanGenerationConstraints.cs`

```csharp
public record PlanGenerationConstraints
{
    public int? MaxSteps { get; init; }
    public IReadOnlyList<StepType>? AllowedStepTypes { get; init; }
    public IReadOnlyList<string>? AvailableTools { get; init; }
    public int? MaxSubPlanDepth { get; init; }
    public string? AdditionalInstructions { get; init; }
}
```

---

## Implementation

### File: `src/Content/Infrastructure/Infrastructure.AI/Planner/LlmPlanGeneratorService.cs`

**Project:** `Infrastructure.AI`
**Namespace:** `Infrastructure.AI.Planner`

#### Constructor Dependencies

- `IChatClientFactory` -- to obtain an `IChatClient` for structured output generation
- `IPlanValidator` -- to validate the generated plan before returning
- `IOptionsMonitor<PlannerOptions>` -- for default model deployment key, temperature, generation settings
- `ILogger<LlmPlanGeneratorService>`

#### GenerateAsync Flow

1. **Build the generation prompt** -- System message containing JSON schema describing `PlanGraph` structure, all step types, edge types, configuration subtypes. Instructions for valid JSON output.

2. **Build the user message** -- Task description, optionally augmented with `AdditionalInstructions` from constraints.

3. **Obtain a chat client** -- Call `IChatClientFactory.GetChatClientAsync()` using `PlannerOptions.GenerationModel`.

4. **Send the request** -- Call `IChatClient.GetResponseAsync()` (not streaming -- need complete response for JSON parsing). Set `ChatOptions.ResponseFormat` to `ChatResponseFormat.Json` if supported.

5. **Extract the response text** -- Follow the `ExtractContent()` pattern from `RunOrchestratedTaskCommandHandler`.

6. **Deserialize the JSON** -- Use `System.Text.Json.JsonSerializer.Deserialize<LlmPlanOutput>()`. Wrap in try/catch; on `JsonException`, return `Result<PlanGraph>.Fail(...)`.

7. **Assign IDs** -- Post-process: assign `PlanId.New()`, `PlanStepId.New()` for each step, resolve edge name-to-ID references.

8. **Validate** -- Call `IPlanValidator.ValidateAsync(generatedPlan, ct)`. If fails, return `Result<PlanGraph>.Fail()`.

9. **Return** -- `Result<PlanGraph>.Success(validatedPlan)`.

#### System Prompt Design

The system prompt includes a condensed JSON schema for the `PlanGraph` output format:

```
Output format:
{
  "name": "string",
  "steps": [
    {
      "name": "string",
      "type": "LlmCall | ToolUse | HumanGate | ConditionalBranch | SubPlanInvocation",
      "configuration": { ... type-specific config ... },
      "retryPolicy": { "maxRetries": 3, "strategy": "Exponential", "onExhausted": "FailStep" },
      "timeoutSeconds": 60
    }
  ],
  "edges": [
    { "from": "step-name-1", "to": "step-name-2", "type": "DataFlow | ControlFlow | ConditionalTrue | ConditionalFalse" }
  ],
  "configuration": { "planTimeoutMinutes": 30, "maxParallelSteps": 10, "maxSubPlanDepth": 5 }
}
```

Plus descriptions of each `StepConfiguration` subtype's fields.

#### Intermediate Deserialization Model

Because the LLM outputs step names in edges rather than GUIDs:

```csharp
internal record LlmPlanOutput
{
    public string Name { get; init; } = "";
    public IReadOnlyList<LlmStepOutput> Steps { get; init; } = [];
    public IReadOnlyList<LlmEdgeOutput> Edges { get; init; } = [];
    public LlmPlanConfigOutput? Configuration { get; init; }
}

internal record LlmStepOutput
{
    public string Name { get; init; } = "";
    public string Type { get; init; } = "";
    public JsonElement Configuration { get; init; }
    public LlmRetryPolicyOutput? RetryPolicy { get; init; }
    public int TimeoutSeconds { get; init; } = 60;
}

internal record LlmEdgeOutput
{
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public string Type { get; init; } = "ControlFlow";
    public string? Condition { get; init; }
}
```

#### Mapping LlmPlanOutput to PlanGraph

Private method `MapToPlanGraph(LlmPlanOutput output)`:

1. Assign `PlanId.New()`
2. For each step: parse `StepType`, assign `PlanStepId.New()`, deserialize `Configuration` to correct subtype
3. Build name-to-ID dictionary
4. For each edge: resolve `From`/`To` names to `PlanStepId`, parse `EdgeType`
5. Map `Configuration` or use defaults
6. Return `PlanGraph`

#### Error Handling

- Non-JSON response -> `Result<PlanGraph>.Fail("LLM did not return valid JSON")`
- Schema mismatch -> `Result<PlanGraph>.Fail("Failed to parse plan: {details}")`
- Unknown step name in edge -> Let validator catch it
- Validation fails -> Forward validator's errors
- LLM exception -> `Result<PlanGraph>.Fail("Plan generation failed: {message}")`

---

## Configuration (Section 16)

`LlmPlanGeneratorService` reads from `PlannerOptions`:
- `GenerationModel: string?` -- model deployment key
- `GenerationTemperature: double` -- default 0.3
- `GenerationMaxTokens: int` -- default 4096

---

## DI Registration (Section 15)

```csharp
services.AddScoped<IPlanGenerator, LlmPlanGeneratorService>();
```

---

## File Summary

| File | Action | Description |
|------|--------|-------------|
| `src/Content/Domain/Domain.AI/Planner/PlanGenerationConstraints.cs` | Existed | Optional constraints record (created in Section 02) |
| `src/Content/Infrastructure/Infrastructure.AI/Planner/LlmPlanGeneratorService.cs` | Create | `IPlanGenerator` implementation — orchestration only |
| `src/Content/Infrastructure/Infrastructure.AI/Planner/LlmPlanOutputMapper.cs` | Create | Static mapping logic (two-pass ID assignment) |
| `src/Content/Infrastructure/Infrastructure.AI/Planner/LlmPlanOutput.cs` | Create | Internal intermediate DTOs |
| `src/Content/Infrastructure/Infrastructure.AI/Planner/PlannerOptions.cs` | Create | Configuration options (model, temperature, max tokens) |
| `src/Content/Infrastructure/Infrastructure.AI/Planner/JsonElementExtensions.cs` | Create | Helper for safe JsonElement property extraction |
| `src/Content/Tests/Infrastructure.AI.Tests/Planner/LlmPlanGeneratorServiceTests.cs` | Create | 8 unit tests |

---

## Key Design Decisions

1. **Intermediate DTO, not direct deserialization** -- LLMs produce human-readable names, not GUIDs. Deserialize to `LlmPlanOutput` first, then map to `PlanGraph` with proper IDs.

2. **Two-pass ID assignment** -- All step IDs are assigned in a first pass before building step objects. This ensures ConditionalBranch targets can forward-reference steps declared later in the list.

3. **No streaming** -- Plan generation requires the full JSON response for parsing. `GetResponseAsync` not `GetStreamingResponseAsync`.

4. **Validation is mandatory** -- Even with schema guidance, LLMs can produce invalid DAGs. `IPlanValidator` call is not optional.

5. **Schema in prompt, not `JsonSchemaExporter`** -- Hand-crafted schema in the system prompt produces better LLM output than machine-generated JSON Schema.

6. **JSON fence sanitization** -- LLMs frequently wrap output in ```json fences despite instructions. `SanitizeJsonResponse` strips these before deserialization.

7. **Mapper extracted for SRP** -- Service handles orchestration (~120 lines), mapper handles deserialization/mapping (~200 lines). Follows one-class-per-file convention.

8. **Input validation at boundary** -- `taskDescription` validated with `ArgumentException.ThrowIfNullOrWhiteSpace` since this is a system boundary where untrusted input enters.
