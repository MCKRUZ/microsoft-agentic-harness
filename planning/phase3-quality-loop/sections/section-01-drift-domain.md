# Section 1: Drift Detection Domain Models

## Status: IMPLEMENTED

## Overview

This section creates the foundational domain vocabulary for the drift detection subsystem. All types live in the `Domain.AI` project under a new `DriftDetection/` directory. Every type is an immutable record with init-only properties, following the same conventions established by the `Escalation/` directory (e.g., `EscalationRequest`, `EscalationOutcome`, `RiskLevel`).

These domain models have **no dependencies** on other sections or external packages. They are pure domain types that later sections (config, interfaces, services) build upon.

## Implementation Deviations

1. **`DriftAuditRecord.Data` renamed to `Payload`** — matches `EscalationAuditRecord.Payload` for cross-domain consistency.
2. **`DriftScope` enum given explicit integer values** (`Agent = 0, Skill = 1, TaskType = 2`) — self-documents specificity ordering.
3. **`DriftAuditRecord.Payload` given `<remarks>` block** — documents which `RecordType` maps to which deserialization target, matching `EscalationAuditRecord` pattern.
4. **`DriftAuditRecord.RecordId` kept** — intentional divergence from `EscalationAuditRecord` (which lacks own ID). Better design; backfill to Escalation deferred.

## Test Results

- 33 tests passing, 0 failures
- All types covered: enums (member existence, ordering), records (construction, round-trip, JSON serialization), immutability checks

## Dependencies

- **None.** This is a leaf section with no prerequisites.
- **Blocked by this section:** section-03 (Drift Config), section-05 (Drift Interfaces), section-07 (EWMA Scorer)

## File Inventory

All files created under:
`src/Content/Domain/Domain.AI/DriftDetection/`

| File | Type | Purpose |
|------|------|---------|
| `DriftDimension.cs` | enum | Quality scoring dimensions |
| `DriftSeverity.cs` | enum | Tiered response semantics |
| `DriftScope.cs` | enum | Baseline hierarchy levels |
| `DriftDimensionScore.cs` | record | Current vs baseline comparison for one dimension |
| `DriftBaseline.cs` | record | "Known good" quality snapshot |
| `DriftScore.cs` | record | Single evaluation's drift measurement |
| `DriftEvent.cs` | record | Detected drift occurrence (persisted as graph node) |
| `DriftResolution.cs` | record + enum | Resolution info + `DriftResolutionType` enum |
| `DriftAuditRecord.cs` | record + enum | Compliance trail + `DriftAuditRecordType` enum |

Test file created under:
`src/Content/Tests/Domain.AI.Tests/DriftDetection/DriftDetectionDomainModelTests.cs`

## Tests (Write First)

Create `src/Content/Tests/Domain.AI.Tests/DriftDetection/DriftDetectionDomainModelTests.cs` in the existing `Domain.AI.Tests` project. Follow the pattern from `EscalationDomainModelTests.cs`: xUnit `[Fact]` methods, direct construction of records, assertions on property values and defaults.

```csharp
using Domain.AI.DriftDetection;
using Xunit;

namespace Domain.AI.Tests.DriftDetection;

public sealed class DriftDetectionDomainModelTests
{
    // --- DriftDimension enum ---

    // Test: DriftDimension enum has exactly 6 members matching the spec:
    //   Faithfulness, Relevance, StructuralConformance, ToolUsageAccuracy, Coherence, InstructionFollowing

    // --- DriftSeverity enum ---

    // Test: DriftSeverity enum values are ordered None(0) < Warn(1) < Alert(2) < Escalate(3)
    //   Assert integer casting produces the expected ordering.

    // --- DriftScope enum ---

    // Test: DriftScope enum has Agent, Skill, TaskType members matching the hierarchy

    // --- DriftDimensionScore ---

    // Test: DriftDimensionScore construction with all fields populated
    //   Create a record with CurrentValue=0.7, BaselineValue=0.85, EwmaValue=0.78, Deviation=1.5
    //   Assert all properties round-trip correctly.

    // Test: DriftDimensionScore deviation calculation is correct for known inputs
    //   (This validates that the record stores deviation -- actual calculation is in section-07)

    // --- DriftBaseline ---

    // Test: DriftBaseline construction with all fields populated
    //   Create with scope, dimensions dictionary, sigma dictionary, sample count, window dates.
    //   Assert all properties set correctly, BaselineId is non-empty Guid.

    // Test: DriftBaseline immutability -- IReadOnlyDictionary prevents external mutation
    //   Verify the Dimensions and DimensionSigmas properties are IReadOnlyDictionary.

    // --- DriftScore ---

    // Test: DriftScore severity assignment matches expected enum value
    //   Create with Severity = DriftSeverity.Alert, verify it round-trips.

    // Test: DriftScore OverallDrift stores the max deviation across dimensions

    // --- DriftEvent ---

    // Test: DriftEvent with null Resolution represents unresolved drift
    //   Create with Resolution = null, assert Resolution is null and other fields are set.

    // Test: DriftEvent with Resolution populated represents resolved drift

    // --- DriftResolution ---

    // Test: DriftResolutionType enum covers all expected resolution paths:
    //   LearningApplied, BaselineAdjusted, ManualDismissal, EscalationResolved

    // Test: DriftResolution construction with each resolution type

    // --- DriftAuditRecord ---

    // Test: DriftAuditRecordType enum has Detected, Resolved, BaselineUpdated, EscalationTriggered

    // Test: DriftAuditRecord construction with JSON payload
    //   Create with RecordType = Detected, Data = serialized JSON, verify round-trip.

    // Test: DriftAuditRecord serializes to/from JSON correctly
    //   Use System.Text.Json to serialize and deserialize, verify equality.
}
```

Each comment block above becomes a `[Fact]` test method. Use the naming convention `TypeName_Scenario_ExpectedResult` (e.g., `DriftSeverity_IntegerCasting_MaintainsOrdering`).

## Implementation Details

### Namespace

All types use `namespace Domain.AI.DriftDetection;` -- file-scoped namespace, no curly braces. This follows the pattern of `Domain.AI.Escalation`, `Domain.AI.Governance`, etc.

### DriftDimension.cs

```csharp
namespace Domain.AI.DriftDetection;

/// <summary>
/// Quality scoring dimensions tracked by drift detection.
/// Each dimension represents an independent axis of agent output quality
/// that can be measured and compared against a baseline.
/// </summary>
public enum DriftDimension
{
    /// <summary>Whether agent output is factually consistent with source material.</summary>
    Faithfulness,
    /// <summary>Whether agent output addresses the user's actual question/intent.</summary>
    Relevance,
    /// <summary>Whether output follows expected structural patterns (formatting, schema).</summary>
    StructuralConformance,
    /// <summary>Whether tools are invoked correctly with valid arguments.</summary>
    ToolUsageAccuracy,
    /// <summary>Logical consistency and flow within the output.</summary>
    Coherence,
    /// <summary>Whether the agent follows system prompt and skill instructions.</summary>
    InstructionFollowing
}
```

### DriftSeverity.cs

```csharp
namespace Domain.AI.DriftDetection;

/// <summary>
/// Tiered severity levels for detected drift, driving escalation behavior.
/// Integer values encode ordering so that <c>DriftSeverity.Warn &lt; DriftSeverity.Alert</c> is valid.
/// </summary>
public enum DriftSeverity
{
    /// <summary>No significant drift detected.</summary>
    None = 0,
    /// <summary>Drift exceeds warning threshold. Logged and notified.</summary>
    Warn = 1,
    /// <summary>Drift exceeds alert threshold. Requires attention.</summary>
    Alert = 2,
    /// <summary>Drift exceeds escalation threshold. Triggers Phase 2 escalation if enabled.</summary>
    Escalate = 3
}
```

### DriftScope.cs

```csharp
namespace Domain.AI.DriftDetection;

/// <summary>
/// Hierarchy level at which a drift baseline is defined.
/// Baselines cascade: TaskType -> Skill -> Agent (most specific wins).
/// </summary>
public enum DriftScope
{
    /// <summary>Agent-wide baseline (broadest scope).</summary>
    Agent,
    /// <summary>Skill-specific baseline.</summary>
    Skill,
    /// <summary>Task-type-specific baseline (most granular).</summary>
    TaskType
}
```

### DriftDimensionScore.cs

```csharp
namespace Domain.AI.DriftDetection;

/// <summary>
/// Holds the current vs baseline comparison for a single <see cref="DriftDimension"/>.
/// Produced by <c>IDriftScorer</c> during drift evaluation.
/// </summary>
public sealed record DriftDimensionScore
{
    /// <summary>The raw score value from the current evaluation.</summary>
    public required double CurrentValue { get; init; }

    /// <summary>The baseline mean for this dimension.</summary>
    public required double BaselineValue { get; init; }

    /// <summary>The EWMA-smoothed value after incorporating this evaluation.</summary>
    public required double EwmaValue { get; init; }

    /// <summary>
    /// Deviation from baseline in sigma units. Drives severity classification.
    /// Computed as <c>abs(ewma - baselineMean) / sigma</c>.
    /// </summary>
    public required double Deviation { get; init; }
}
```

### DriftBaseline.cs

```csharp
namespace Domain.AI.DriftDetection;

/// <summary>
/// A "known good" quality snapshot for a scope. Drift scores are compared against
/// the baseline's per-dimension means and standard deviations to determine whether
/// quality has degraded.
/// </summary>
/// <remarks>
/// Baselines are stored as knowledge graph nodes with deterministic IDs
/// (<c>"driftbaseline:{scope}:{identifier}"</c>) for O(1) lookup.
/// A new baseline overwrites the previous one; history is tracked via
/// <see cref="DriftAuditRecord"/> entries with <see cref="DriftAuditRecordType.BaselineUpdated"/>.
/// </remarks>
public sealed record DriftBaseline
{
    /// <summary>Unique identifier for this baseline snapshot.</summary>
    public required Guid BaselineId { get; init; }

    /// <summary>The hierarchy level of this baseline.</summary>
    public required DriftScope Scope { get; init; }

    /// <summary>
    /// Identifies the entity within the scope (agent ID, skill name, or task type name).
    /// </summary>
    public required string ScopeIdentifier { get; init; }

    /// <summary>Per-dimension mean scores from the baseline window.</summary>
    public required IReadOnlyDictionary<DriftDimension, double> Dimensions { get; init; }

    /// <summary>Per-dimension standard deviations from the baseline window.</summary>
    public required IReadOnlyDictionary<DriftDimension, double> DimensionSigmas { get; init; }

    /// <summary>Number of evaluations used to compute this baseline.</summary>
    public required int SampleCount { get; init; }

    /// <summary>Start of the rolling window used for this baseline.</summary>
    public required DateTimeOffset WindowStart { get; init; }

    /// <summary>End of the rolling window used for this baseline.</summary>
    public required DateTimeOffset WindowEnd { get; init; }

    /// <summary>When this baseline was created.</summary>
    public required DateTimeOffset CreatedAt { get; init; }
}
```

### DriftScore.cs

```csharp
namespace Domain.AI.DriftDetection;

/// <summary>
/// The drift measurement result for a single evaluation, comparing per-dimension
/// scores against a <see cref="DriftBaseline"/>. Produced by <c>IDriftDetectionService</c>.
/// </summary>
public sealed record DriftScore
{
    /// <summary>Unique identifier for this score.</summary>
    public required Guid ScoreId { get; init; }

    /// <summary>The baseline this score was compared against.</summary>
    public required Guid BaselineId { get; init; }

    /// <summary>The scope of the evaluation.</summary>
    public required DriftScope Scope { get; init; }

    /// <summary>Identifies the entity within the scope.</summary>
    public required string ScopeIdentifier { get; init; }

    /// <summary>Per-dimension comparison results.</summary>
    public required IReadOnlyDictionary<DriftDimension, DriftDimensionScore> Dimensions { get; init; }

    /// <summary>
    /// Maximum deviation across all dimensions (in sigma units).
    /// This single value drives the severity classification.
    /// </summary>
    public required double OverallDrift { get; init; }

    /// <summary>Classified severity based on threshold configuration.</summary>
    public required DriftSeverity Severity { get; init; }

    /// <summary>When this score was computed.</summary>
    public required DateTimeOffset ScoredAt { get; init; }
}
```

### DriftEvent.cs

```csharp
namespace Domain.AI.DriftDetection;

/// <summary>
/// A detected drift occurrence, persisted as a knowledge graph node.
/// Links to the <see cref="DriftScore"/> that triggered it and optionally
/// to a <see cref="DriftResolution"/> when the drift is addressed.
/// </summary>
public sealed record DriftEvent
{
    /// <summary>Unique identifier for this event.</summary>
    public required Guid EventId { get; init; }

    /// <summary>The drift score that triggered this event.</summary>
    public required DriftScore DriftScore { get; init; }

    /// <summary>
    /// How this drift was resolved. Null while the drift is still outstanding.
    /// </summary>
    public DriftResolution? Resolution { get; init; }

    /// <summary>When the drift was first detected.</summary>
    public required DateTimeOffset DetectedAt { get; init; }
}
```

### DriftResolution.cs

```csharp
namespace Domain.AI.DriftDetection;

/// <summary>
/// How a detected drift was ultimately resolved.
/// </summary>
public enum DriftResolutionType
{
    /// <summary>A learning entry was applied that corrected the drift cause.</summary>
    LearningApplied,
    /// <summary>The baseline was adjusted to reflect intentional quality changes.</summary>
    BaselineAdjusted,
    /// <summary>An operator manually dismissed the drift as a false positive.</summary>
    ManualDismissal,
    /// <summary>A Phase 2 escalation resolved the underlying issue.</summary>
    EscalationResolved
}

/// <summary>
/// Records how and when a <see cref="DriftEvent"/> was resolved.
/// </summary>
public sealed record DriftResolution
{
    /// <summary>The mechanism by which this drift was resolved.</summary>
    public required DriftResolutionType ResolvedBy { get; init; }

    /// <summary>
    /// Identifier linking to the resolving entity (learning ID, escalation ID, etc.).
    /// </summary>
    public required string ResolutionId { get; init; }

    /// <summary>When the drift was resolved.</summary>
    public required DateTimeOffset ResolvedAt { get; init; }
}
```

### DriftAuditRecord.cs

```csharp
namespace Domain.AI.DriftDetection;

/// <summary>
/// Discriminator for <see cref="DriftAuditRecord"/> entries.
/// Determines how the <see cref="DriftAuditRecord.Data"/> field should be interpreted.
/// </summary>
public enum DriftAuditRecordType
{
    /// <summary>A drift event was detected.</summary>
    Detected,
    /// <summary>A drift event was resolved.</summary>
    Resolved,
    /// <summary>A baseline was updated (recalculated or adjusted).</summary>
    BaselineUpdated,
    /// <summary>An escalation was triggered from a drift event.</summary>
    EscalationTriggered
}

/// <summary>
/// A single audit log entry for a drift detection lifecycle event.
/// Used by <c>IDriftAuditStore</c> for append-only JSONL persistence.
/// The <see cref="Data"/> field contains the serialized event data,
/// discriminated by <see cref="RecordType"/>.
/// </summary>
public sealed record DriftAuditRecord
{
    /// <summary>Unique identifier for this audit record.</summary>
    public required Guid RecordId { get; init; }

    /// <summary>Correlates to the originating drift event.</summary>
    public required Guid EventId { get; init; }

    /// <summary>Discriminator for deserialization of <see cref="Data"/>.</summary>
    public required DriftAuditRecordType RecordType { get; init; }

    /// <summary>
    /// Serialized JSON payload containing event-specific data.
    /// Deserialization target depends on <see cref="RecordType"/>.
    /// </summary>
    public required string Data { get; init; }

    /// <summary>When this audit record was created.</summary>
    public required DateTimeOffset RecordedAt { get; init; }
}
```

## Conventions and Patterns to Follow

1. **File-scoped namespaces** -- `namespace Domain.AI.DriftDetection;` (no braces), matching all other Domain.AI files.
2. **`sealed record`** for all non-enum types -- matches `EscalationRequest`, `EscalationOutcome`, `EscalationAuditRecord`, etc.
3. **`required` keyword** on properties that must always be provided at construction time. Optional properties (like `DriftEvent.Resolution`) omit `required` and default to `null`.
4. **`IReadOnlyDictionary<K,V>`** and **`IReadOnlyList<T>`** for collection properties -- never expose mutable collections on public surfaces.
5. **Full XML documentation** on every public type and member -- this is a template project; docs are teaching material.
6. **One logical group per file** -- enums can share a file with their closely-related record (e.g., `DriftResolutionType` + `DriftResolution` in one file, `DriftAuditRecordType` + `DriftAuditRecord` in one file). Standalone enums get their own file.
7. **No framework dependencies** -- these are pure domain types. No `using` statements beyond `System` implicit usings.
8. **Integer-backed enum ordering** for `DriftSeverity` -- enables `severity >= DriftSeverity.Warn` comparisons in service code (sections 7-8).

## Verification

After implementing, run:
```
dotnet build src/Content/Domain/Domain.AI/Domain.AI.csproj
dotnet test src/Content/Tests/Domain.AI.Tests/Domain.AI.Tests.csproj
```

All new tests should pass. No existing tests should break since these are purely additive types with no modifications to existing files.
