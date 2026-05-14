# Section 2: Learnings Log Domain Models

## Status: IMPLEMENTED

## Implementation Deviations

1. **`LearningCategory` and `LearningSourceType` enums given explicit integer values** (0-4) — matches convention established in Section 01 for serialization stability.
2. **`LearningCategory` XML doc updated** — plain-text `->` arrows replaced with `<see cref="DecayClass.Permanent"/>` cross-references for consistency.
3. **Added 2 enum count tests** — `DecayClass_HasExactlyThreeMembers` and `LearningSourceType_HasExactlyFiveMembers` for consistent coverage with Section 01 pattern.

## Test Results

- 28 tests passing, 0 failures
- All types covered: enums (member existence, count, ordering), records (construction, defaults, round-trip), scope combinations

## Overview

This section creates the core domain vocabulary for the learnings system. All types live in the `Domain.AI` layer under a new `Learnings/` directory. They are pure domain concepts with zero framework dependencies -- only enums and immutable records with init-only properties.

These models are consumed by every downstream learnings section (config, interfaces, stores, handlers, SSE notifiers, bridges). Nothing in this section depends on any other Phase 3 section; it can be implemented in parallel with section-01 (Drift Detection Domain Models).

**Dependencies:** None.
**Blocks:** section-04 (LearningsConfig), section-06 (Learnings Interfaces), section-11 (Decay Service).

---

## File Organization

All files go under:
```
src/Content/Domain/Domain.AI/Learnings/
```

One file per type or logical group. Namespace: `Domain.AI.Learnings`.

| File | Type | Kind |
|------|------|------|
| `LearningCategory.cs` | `LearningCategory` | enum |
| `DecayClass.cs` | `DecayClass` | enum |
| `LearningSourceType.cs` | `LearningSourceType` | enum |
| `LearningScope.cs` | `LearningScope` | record |
| `LearningSource.cs` | `LearningSource` | record |
| `LearningProvenance.cs` | `LearningProvenance` | record |
| `LearningEntry.cs` | `LearningEntry` | record |
| `WeightedLearning.cs` | `WeightedLearning` | record |

Test file:
```
src/Content/Tests/Domain.AI.Tests/Learnings/LearningsDomainModelTests.cs
```

---

## Tests (Write First)

Create `src/Content/Tests/Domain.AI.Tests/Learnings/LearningsDomainModelTests.cs` following the pattern established by `EscalationDomainModelTests.cs`. Use xUnit `[Fact]` attributes, direct assertions.

### Test stubs to implement

```csharp
namespace Domain.AI.Tests.Learnings;

public sealed class LearningsDomainModelTests
{
    // --- LearningEntry ---

    // Test: LearningEntry_WithAllFields_RoundTrips
    //   Construct a LearningEntry with every property set. Assert all properties read back correctly.

    // Test: LearningEntry_DefaultFeedbackWeight_IsOne
    //   Construct with only required properties. Assert FeedbackWeight == 1.0 and UpdateCount == 0.

    // --- LearningScope ---

    // Test: LearningScope_WithOnlyAgentId_HasAgentIdSet
    //   Create scope with AgentId = "agent-1", TeamId = null, IsGlobal = false.

    // Test: LearningScope_WithOnlyTeamId_HasTeamIdSet
    //   Create scope with TeamId = "team-1", AgentId = null, IsGlobal = false.

    // Test: LearningScope_WithIsGlobalTrue_AndNoAgentOrTeam
    //   Create scope with IsGlobal = true, AgentId = null, TeamId = null.

    // Test: LearningScope_WithAllThreeSet_AllAccessible
    //   Create scope with AgentId = "a", TeamId = "t", IsGlobal = true. All three readable.

    // --- Enums ---

    // Test: DecayClass_EnumValues_MatchExpectedSemantics
    //   Assert Volatile, Stable, Permanent exist. Assert ordering: Volatile=0, Stable=1, Permanent=2.

    // Test: LearningCategory_EnumCoversAllCategories
    //   Assert all five values exist: FactualCorrection, StylePreference, ToolUsagePattern,
    //   DomainKnowledge, InstructionUpdate.

    // Test: LearningSourceType_EnumCoversAllTypes
    //   Assert HumanCorrection, DriftDetection, EscalationResolution,
    //   AgentSelfImprovement, ManualEntry all exist.

    // --- WeightedLearning ---

    // Test: WeightedLearning_FinalScoreCalculation_MatchesExpectedFormula
    //   Construct with known RelevanceScore, FeedbackScore, FreshnessScore, FinalScore.
    //   Verify FinalScore stores the provided value (pre-computed by handler, not record).

    // --- LearningSource ---

    // Test: LearningSource_ConstructionForEachSourceType
    //   Create a LearningSource for each LearningSourceType value. Assert round-trip.

    // --- LearningProvenance ---

    // Test: LearningProvenance_WithConfidence_StoresRawValue
    //   Provenance stores the raw value. The validator (section 06) enforces [0, 1].
    //   Test that 0.85 stores as 0.85 and 0.0 stores as 0.0.
}
```

---

## Implementation Details

### Enums

**`LearningCategory.cs`**

```csharp
namespace Domain.AI.Learnings;

/// <summary>
/// Classifies the type of knowledge a learning entry represents.
/// The category drives default <see cref="DecayClass"/> assignment:
/// <see cref="FactualCorrection"/> -> Permanent,
/// <see cref="StylePreference"/> -> Stable, etc.
/// </summary>
public enum LearningCategory
{
    /// <summary>A correction to factual output (wrong date, name, API signature).</summary>
    FactualCorrection,
    /// <summary>A user preference for tone, format, or style.</summary>
    StylePreference,
    /// <summary>A pattern about when/how to use a specific tool.</summary>
    ToolUsagePattern,
    /// <summary>Domain-specific knowledge not in training data.</summary>
    DomainKnowledge,
    /// <summary>An update to standing instructions or behavioral rules.</summary>
    InstructionUpdate
}
```

**`DecayClass.cs`**

```csharp
namespace Domain.AI.Learnings;

/// <summary>
/// Determines the temporal decay rate for a learning entry.
/// <see cref="Volatile"/> entries expire quickly (default 7 days),
/// <see cref="Stable"/> entries persist longer (default 180 days),
/// and <see cref="Permanent"/> entries never decay.
/// Shelf lives are configurable via <c>LearningsConfig</c>.
/// </summary>
public enum DecayClass
{
    /// <summary>Short-lived knowledge. Decays linearly over VolatileShelfLifeDays.</summary>
    Volatile = 0,
    /// <summary>Long-lived knowledge. Decays linearly over StableShelfLifeDays.</summary>
    Stable = 1,
    /// <summary>Immortal knowledge. Freshness always returns 1.0.</summary>
    Permanent = 2
}
```

**`LearningSourceType.cs`**

```csharp
namespace Domain.AI.Learnings;

/// <summary>
/// Identifies the origin of a learning entry. Used by <c>DriftEscalationBridge</c>
/// to filter drift-originated learnings and by audit queries for provenance reporting.
/// </summary>
public enum LearningSourceType
{
    /// <summary>A human user explicitly corrected agent output.</summary>
    HumanCorrection,
    /// <summary>Drift detection identified a quality regression and generated a corrective learning.</summary>
    DriftDetection,
    /// <summary>An escalation was resolved with corrections that became a learning.</summary>
    EscalationResolution,
    /// <summary>The agent identified its own mistake and self-corrected.</summary>
    AgentSelfImprovement,
    /// <summary>A learning was manually entered by an operator or admin.</summary>
    ManualEntry
}
```

### Records

**`LearningScope.cs`**

```csharp
namespace Domain.AI.Learnings;

/// <summary>
/// Defines the visibility scope for a learning entry using a 3-tier hierarchy:
/// agent-specific -> team-wide -> global. A learning scoped to agent "X" in team "T"
/// is visible only to agent X. A team-scoped learning is visible to all agents in
/// team T. A global learning is visible to all agents.
/// </summary>
/// <remarks>
/// Scope resolution during recall: if querying for agent "X" in team "T", the store
/// returns learnings scoped to X, learnings scoped to T, and global learnings --
/// merging all levels with deduplication by <see cref="LearningEntry.LearningId"/>.
/// At least one of <see cref="AgentId"/>, <see cref="TeamId"/>, or <see cref="IsGlobal"/>
/// must be set. Validation is enforced by <c>RememberCommandValidator</c> (section 06).
/// </remarks>
public sealed record LearningScope
{
    /// <summary>Scopes the learning to a specific agent. Null means not agent-scoped.</summary>
    public string? AgentId { get; init; }

    /// <summary>Scopes the learning to a team of agents. Null means not team-scoped.</summary>
    public string? TeamId { get; init; }

    /// <summary>When true, the learning is visible to all agents regardless of team.</summary>
    public bool IsGlobal { get; init; }
}
```

**`LearningSource.cs`**

```csharp
namespace Domain.AI.Learnings;

/// <summary>
/// Identifies what created a learning entry -- a human correction, drift event,
/// escalation resolution, or agent self-improvement. The <see cref="SourceId"/>
/// correlates back to the originating entity (e.g., escalation ID, drift event ID).
/// </summary>
public sealed record LearningSource
{
    /// <summary>The origin type that produced this learning.</summary>
    public required LearningSourceType SourceType { get; init; }

    /// <summary>
    /// Identifier of the originating entity (escalation ID, drift event ID, user session ID).
    /// Used for audit trail correlation.
    /// </summary>
    public required string SourceId { get; init; }

    /// <summary>Human-readable description of how this learning was created.</summary>
    public required string SourceDescription { get; init; }
}
```

**`LearningProvenance.cs`**

```csharp
namespace Domain.AI.Learnings;

/// <summary>
/// Detailed provenance metadata for a learning entry, tracking which pipeline and task
/// produced the knowledge and with what confidence.
/// </summary>
public sealed record LearningProvenance
{
    /// <summary>The pipeline that produced this learning (e.g., "escalation_resolution", "drift_correction").</summary>
    public required string OriginPipeline { get; init; }

    /// <summary>The specific task within the pipeline (e.g., "human_review", "auto_correct").</summary>
    public required string OriginTask { get; init; }

    /// <summary>When the originating event occurred.</summary>
    public required DateTimeOffset OriginTimestamp { get; init; }

    /// <summary>
    /// Confidence in the learning's correctness, normalized to 0.0-1.0.
    /// Validated by <c>RememberCommandValidator</c> to enforce the [0, 1] range.
    /// </summary>
    public required double Confidence { get; init; }
}
```

**`LearningEntry.cs`**

```csharp
namespace Domain.AI.Learnings;

/// <summary>
/// The core learning record, representing a piece of knowledge captured from corrections,
/// drift events, escalation resolutions, or manual entries. Persisted as a graph node by
/// <c>GraphLearningsStore</c> with deterministic ID <c>"learning:{LearningId}"</c>.
/// </summary>
/// <remarks>
/// <see cref="FeedbackWeight"/> is updated via exponential moving average in
/// <c>ImproveLearningCommandHandler</c>. Higher weights indicate learnings that have been
/// repeatedly validated as useful. The weight influences recall ranking via the formula:
/// <c>finalScore = (1 - alpha) * relevance + alpha * min(feedback * freshness, ceiling)</c>.
/// <see cref="DecayClass"/> determines temporal decay behavior. <see cref="LastReinforcedAt"/>
/// resets the decay clock when a learning receives positive feedback.
/// </remarks>
public sealed record LearningEntry
{
    /// <summary>Unique identifier for this learning.</summary>
    public required Guid LearningId { get; init; }

    /// <summary>What kind of knowledge this learning represents.</summary>
    public required LearningCategory Category { get; init; }

    /// <summary>How quickly this learning decays over time.</summary>
    public required DecayClass DecayClass { get; init; }

    /// <summary>Visibility scope (agent, team, or global).</summary>
    public required LearningScope Scope { get; init; }

    /// <summary>The actual knowledge content -- a natural language description of what was learned.</summary>
    public required string Content { get; init; }

    /// <summary>What produced this learning.</summary>
    public required LearningSource Source { get; init; }

    /// <summary>Pipeline provenance metadata.</summary>
    public required LearningProvenance Provenance { get; init; }

    /// <summary>
    /// EMA-weighted feedback score. Default 1.0 (neutral). Updated by
    /// <c>ImproveLearningCommandHandler</c>. Range: 0.0+ (no upper bound enforced at
    /// domain level; ceiling applied during recall scoring).
    /// </summary>
    public double FeedbackWeight { get; init; } = 1.0;

    /// <summary>Number of times this learning's feedback weight has been updated.</summary>
    public int UpdateCount { get; init; }

    /// <summary>When this learning was first created.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>When this learning was last accessed during a recall query. Null if never recalled.</summary>
    public DateTimeOffset? LastAccessedAt { get; init; }

    /// <summary>
    /// When this learning was last reinforced via positive feedback. Null if never reinforced.
    /// Used by <c>DefaultLearningDecayService</c> to reset the decay clock.
    /// </summary>
    public DateTimeOffset? LastReinforcedAt { get; init; }

    /// <summary>Soft-delete flag. Deleted learnings remain in the graph for audit but are excluded from search.</summary>
    public bool IsDeleted { get; init; }

    /// <summary>Reason for soft-deletion. Null when not deleted.</summary>
    public string? DeleteReason { get; init; }
}
```

**`WeightedLearning.cs`**

```csharp
namespace Domain.AI.Learnings;

/// <summary>
/// A learning entry enriched with computed relevance, feedback, and freshness scores.
/// Returned by <c>RecallQueryHandler</c> after the full scoring pipeline:
/// <c>FinalScore = (1 - alpha) * RelevanceScore + alpha * min(FeedbackScore * FreshnessScore, ceiling)</c>.
/// </summary>
/// <remarks>
/// All score fields are pre-computed by the handler. The record is a pure data carrier --
/// it does not calculate <see cref="FinalScore"/> from the component scores.
/// </remarks>
public sealed record WeightedLearning
{
    /// <summary>The underlying learning entry.</summary>
    public required LearningEntry Learning { get; init; }

    /// <summary>Semantic similarity between the recall query and the learning content (0.0-1.0).</summary>
    public required double RelevanceScore { get; init; }

    /// <summary>The learning's EMA-weighted feedback score (from <see cref="LearningEntry.FeedbackWeight"/>).</summary>
    public required double FeedbackScore { get; init; }

    /// <summary>Temporal freshness based on decay class and age (0.0-1.0).</summary>
    public required double FreshnessScore { get; init; }

    /// <summary>
    /// The blended final score used for ranking. Pre-computed by the recall handler.
    /// </summary>
    public required double FinalScore { get; init; }
}
```

---

## Implementation Checklist

1. Create directory `src/Content/Domain/Domain.AI/Learnings/`
2. Create `LearningCategory.cs` -- enum with 5 values
3. Create `DecayClass.cs` -- enum with 3 values (Volatile=0, Stable=1, Permanent=2)
4. Create `LearningSourceType.cs` -- enum with 5 values
5. Create `LearningScope.cs` -- sealed record with `AgentId?`, `TeamId?`, `IsGlobal`
6. Create `LearningSource.cs` -- sealed record with `SourceType`, `SourceId`, `SourceDescription`
7. Create `LearningProvenance.cs` -- sealed record with `OriginPipeline`, `OriginTask`, `OriginTimestamp`, `Confidence`
8. Create `LearningEntry.cs` -- sealed record with all properties, `FeedbackWeight` defaulting to `1.0`
9. Create `WeightedLearning.cs` -- sealed record with `Learning`, score fields, `FinalScore`
10. Create test directory `src/Content/Tests/Domain.AI.Tests/Learnings/`
11. Create `LearningsDomainModelTests.cs` with all test stubs
12. Run `dotnet build src/AgenticHarness.slnx` -- verify 0 errors
13. Run `dotnet test src/AgenticHarness.slnx` -- verify all new tests pass

## Conventions

- **Namespace:** `Domain.AI.Learnings` (matches `Domain.AI.Escalation`, `Domain.AI.KnowledgeGraph.Models` patterns)
- **Records:** `public sealed record` with `required` on mandatory properties. Property initializers for defaults (`FeedbackWeight = 1.0`).
- **XML docs:** Full XML documentation on all public types and members.
- **No framework dependencies:** Domain layer has zero references to MediatR, FluentValidation, or infrastructure packages.
- **Immutability:** All properties are `init`-only. Collections use `IReadOnlyList<T>` or `IReadOnlyDictionary<K,V>`.
- **Enum ordering:** Explicit integer values only where semantic ordering matters.
