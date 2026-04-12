# Section 05: Causal OTel Span Attribution

## Overview

This section extends the existing `ToolEffectivenessProcessor` in `Infrastructure.Observability/` to add causal OpenTelemetry GenAI semantic convention attributes to tool call spans. These attributes connect each tool invocation to the harness optimization context (candidate ID, iteration) and produce consistent span attributes for querying trace history.

**Depends on:** section-04-trace-infrastructure (for `TraceScope` domain model and `IAgentExecutionContext.TraceScope` property)

**Parallelizable with:** section-06-history-store

---

## Background

The existing `ToolEffectivenessProcessor` enriches `execute_tool` spans with effectiveness attributes (`agent.tool.result_empty`, `agent.tool.result_chars`, `agent.tool.result_truncated`). This section adds a second processor — `CausalSpanProcessor` — that adds the causal attribution attributes needed to correlate OTel spans with optimization runs.

No new `ActivitySource` is needed. The new processor follows the exact same `BaseProcessor<Activity>` pattern used by all existing processors in `Infrastructure.Observability/Processors/`.

---

## New Constants in `ToolConventions`

**File to modify:** `src/Content/Domain/Domain.AI/Telemetry/Conventions/ToolConventions.cs`

Add the following constants to the existing `ToolConventions` static class:

```csharp
/// <summary>Gen AI tool name attribute per OTel GenAI semantic conventions.</summary>
public const string GenAiToolName = "gen_ai.tool.name";

/// <summary>SHA-256 hash of the serialized tool input (only when IsAllDataRequested = true).</summary>
public const string ToolInputHash = "tool.input_hash";

/// <summary>Bucketed outcome of the tool call: success, partial, error, timeout, blocked.</summary>
public const string ToolResultCategory = "tool.result_category";

/// <summary>Optimization candidate ID from TraceScope (omitted on non-optimization runs).</summary>
public const string HarnessCandidateId = "gen_ai.harness.candidate_id";

/// <summary>Optimization iteration number from TraceScope (omitted on non-optimization runs).</summary>
public const string HarnessIteration = "gen_ai.harness.iteration";
```

---

## New Processor

**File to create:** `src/Content/Infrastructure/Infrastructure.Observability/Processors/CausalSpanProcessor.cs`

```csharp
/// <summary>
/// Span processor that enriches <c>execute_tool</c> spans with causal
/// OTel GenAI semantic convention attributes, connecting tool invocations
/// to their harness optimization context.
/// </summary>
/// <remarks>
/// Added attributes:
/// <list type="bullet">
///   <item><c>gen_ai.tool.name</c> — canonical tool name per GenAI conventions</item>
///   <item><c>tool.input_hash</c> — SHA-256 of serialized input (only when <c>IsAllDataRequested = true</c>)</item>
///   <item><c>tool.result_category</c> — bucketed outcome: success, partial, error, timeout, blocked</item>
///   <item><c>gen_ai.harness.candidate_id</c> — optimization candidate (omitted on normal runs)</item>
///   <item><c>gen_ai.harness.iteration</c> — optimization iteration (omitted on normal runs)</item>
/// </list>
/// </remarks>
public sealed class CausalSpanProcessor : BaseProcessor<Activity>
```

**Constructor parameter:** `ILogger<CausalSpanProcessor> logger`

**`OnEnd` logic:**

1. Read `gen_ai.operation.name` tag. If not equal to `"execute_tool"`, return immediately — no tags added.
2. Read `agent.tool.name` (already set by `ToolEffectivenessProcessor`) → set `gen_ai.tool.name` to the same value.
3. Compute `tool.input_hash` only when `data.IsAllDataRequested == true`. Input is read from `gen_ai.tool.call.input` tag (string). Hash via `SHA256.HashData(Encoding.UTF8.GetBytes(input))`, format as lowercase hex. If the tag is absent, skip the hash.
4. Read `agent.tool.status` tag → map to `tool.result_category`:
   - `"success"` → `"success"`
   - `"failure"` → `"error"`
   - `"timeout"` → `"timeout"`
   - Anything else → `"partial"`
   - If the span has `ActivityStatusCode.Error` and no explicit status tag → `"error"`
5. Read `gen_ai.harness.candidate_id` and `gen_ai.harness.iteration` tags. These are set upstream by `ToolDiagnosticsMiddleware` (section-04). If present, pass them through unchanged. If absent, do not add the tags.

**Note on `gen_ai.tool.call.input`:** This attribute is set by `ToolDiagnosticsMiddleware` when it records the tool call. If the project does not yet set this attribute, the hash is silently skipped. The processor is defensive.

---

## DI Registration

**File to modify:** `src/Content/Infrastructure/Infrastructure.Observability/DependencyInjection.cs`

Register `CausalSpanProcessor` alongside the existing processors. It should be added after `ToolEffectivenessProcessor` since it reads `agent.tool.name` which `ToolEffectivenessProcessor` does not set (it uses `ToolConventions.Name` directly). Order does not matter for reads from the upstream middleware, but register in logical grouping with the other tool processors.

```csharp
services.AddSingleton<CausalSpanProcessor>();
// Then wire into the OTel builder:
.AddProcessor<CausalSpanProcessor>()
```

---

## Tests

**Test project:** `Infrastructure.Observability.Tests`

**File to create:** `src/Content/Tests/Infrastructure.Observability.Tests/Processors/CausalSpanProcessorTests.cs`

Follow the identical test fixture pattern used in `ToolEffectivenessProcessorTests.cs` — `IDisposable`, `ActivitySource`, `ActivityListener` with `ActivitySamplingResult.AllDataAndRecorded`, `CreateProcessor()` factory method.

### Test stubs

```csharp
public sealed class CausalSpanProcessorTests : IDisposable
{
    // ActivitySource + ActivityListener fixture (same as ToolEffectivenessProcessorTests)

    private static CausalSpanProcessor CreateProcessor() { ... }

    [Fact]
    public void OnEnd_ToolSpan_AddsToolNameTag()
    /// Sets gen_ai.operation.name = execute_tool and agent.tool.name = "file_system"
    /// Asserts gen_ai.tool.name tag equals "file_system"

    [Fact]
    public void OnEnd_ToolSpan_AddsInputHashTag()
    /// Sets gen_ai.operation.name = execute_tool, gen_ai.tool.call.input = "{ \"path\": \"/foo\" }"
    /// Listener must use AllDataAndRecorded (IsAllDataRequested = true)
    /// Asserts tool.input_hash is a 64-char lowercase hex string

    [Fact]
    public void OnEnd_InputHashNotComputed_WhenIsAllDataRequestedFalse()
    /// Listener uses ActivitySamplingResult.AllData (IsAllDataRequested = false)
    /// Sets gen_ai.operation.name = execute_tool with input tag
    /// Asserts tool.input_hash tag is null

    [Fact]
    public void OnEnd_ToolSpan_AddsResultCategoryTag()
    /// Sets gen_ai.operation.name = execute_tool, agent.tool.status = "success"
    /// Asserts tool.result_category = "success"

    [Fact]
    public void OnEnd_ToolSpanWithFailureStatus_MapsToErrorCategory()
    /// Sets agent.tool.status = "failure"
    /// Asserts tool.result_category = "error"

    [Fact]
    public void OnEnd_ToolSpanWithTimeout_MapsToTimeoutCategory()
    /// Sets agent.tool.status = "timeout"
    /// Asserts tool.result_category = "timeout"

    [Fact]
    public void OnEnd_WhenCandidateIdTagPresent_PassesThroughCandidateIdTag()
    /// Sets gen_ai.harness.candidate_id = "abc-123" before OnEnd
    /// Asserts tag still present after OnEnd (processor does not remove it)

    [Fact]
    public void OnEnd_WhenNoCandidateIdTag_DoesNotAddCandidateIdTag()
    /// No gen_ai.harness.candidate_id tag set
    /// Asserts gen_ai.harness.candidate_id tag is null after OnEnd

    [Fact]
    public void OnEnd_NonToolSpan_AddsNoTags()
    /// Sets gen_ai.operation.name = "chat"
    /// Asserts gen_ai.tool.name, tool.input_hash, tool.result_category are all null
}
```

**Key testing note on `IsAllDataRequested`:** The `ActivitySamplingResult` enum controls this. Use `ActivitySamplingResult.AllDataAndRecorded` for the primary `_listener` (sets `IsAllDataRequested = true`). For the hash-guard test, create a second `ActivityListener` with `ActivitySamplingResult.AllData` (sets `IsAllDataRequested = false`) or use `ActivitySamplingResult.PropagationData`. The simplest approach: create a dedicated `ActivitySource` and `ActivityListener` for that single test, configured with the lower sampling result.

---

## File Summary

| Action | File |
|--------|------|
| Modify | `src/Content/Domain/Domain.AI/Telemetry/Conventions/ToolConventions.cs` |
| Create | `src/Content/Infrastructure/Infrastructure.Observability/Processors/CausalSpanAttributionProcessor.cs` |
| Modify | `src/Content/Infrastructure/Infrastructure.Observability/Exporters/ObservabilityTelemetryConfigurator.cs` |
| Create | `src/Content/Tests/Infrastructure.Observability.Tests/Processors/CausalSpanAttributionProcessorTests.cs` |

## Implementation Notes (Actual vs. Planned)

**Class name changed:** `CausalSpanProcessor` → `CausalSpanAttributionProcessor` — more descriptive name matching the processor's specific concern.

**DI registration location:** Plan said `DependencyInjection.cs`. Actual: registered inline in `ObservabilityTelemetryConfigurator.ConfigureTracing()` at pipeline position 5 (after `ToolEffectivenessProcessor`). All infrastructure processors are registered this way to control ordering explicitly.

**Result category mapping:** Span-level categories are narrower than `ExecutionTraceRecord` categories by design. Spans map only `ActivityStatusCode.Ok → "success"` and `ActivityStatusCode.Error → "error"`. The `"partial"`, `"timeout"`, and `"blocked"` values are set on `ExecutionTraceRecord` by `ToolDiagnosticsMiddleware`, not through this processor.

**Input tag name:** Plan referenced `gen_ai.tool.call.input` but implementation uses `gen_ai.tool.call.arguments` (`ToolConventions.ToolCallArguments`) — consistent with the OTel GenAI semantic conventions and what `ToolDiagnosticsMiddleware` sets.

**Candidate/iteration source:** Plan said "read from tags set upstream by `ToolDiagnosticsMiddleware`". Actual: reads from Activity baggage (not tags), which propagates across process/async boundaries automatically via OTel context propagation.

**Tests:** 11 total (9 spec + 2 added in code review for iteration baggage promotion).

**Committed in:** `7205197` (bundled with section-04 commit by previous session).
