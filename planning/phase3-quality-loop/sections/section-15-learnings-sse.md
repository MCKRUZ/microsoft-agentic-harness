# Section 15: AG-UI Learning Events and SSE Notifier

## Overview

This section adds learnings notifications to the AG-UI SSE event stream. It creates three new AG-UI event DTOs (`LearningCapturedEvent`, `LearningAppliedEvent`, `LearningForgottenEvent`) and an `AgUiLearningNotifier` that translates domain learning events into SSE frames. The implementation mirrors the existing `AgUiEscalationNotifier` and the parallel `AgUiDriftNotifier` (section 14) exactly: inject `IAgUiEventWriterAccessor`, gracefully no-op when no AG-UI run is active, and catch non-cancellation exceptions with a logged warning.

**Layer:** Presentation.AgentHub (notifier + event DTOs), Application.AI.Common (interface -- from section 06)

## Dependencies

- **Section 02 (Learnings Domain Models):** `LearningEntry`, `LearningCategory`, `LearningScope`, `LearningSource` records from `Domain.AI/Learnings/`
- **Section 06 (Learnings Interfaces):** `ILearningNotificationChannel` interface defining `NotifyLearningCapturedAsync(LearningEntry, CancellationToken)` and `NotifyLearningAppliedAsync(LearningEntry, string agentId, CancellationToken)`
- **Section 14 (Drift SSE):** Establishes the drift event constants in `AgUiEventType` and drift event DTOs in `AgUiEvents.cs`. This section follows the same modification pattern for learning events.
- **Existing infrastructure:** `IAgUiEventWriterAccessor`, `IAgUiEventWriter`, `AgUiEvent` base record, `AgUiEventType` constants (all in `Presentation.AgentHub.AgUi` namespace)

## Tests First

All tests go in `src/Content/Tests/Presentation.AgentHub.Tests/Notifications/AgUiLearningNotifierTests.cs` and `src/Content/Tests/Presentation.AgentHub.Tests/AgUi/AgUiLearningEventSerializationTests.cs`. Follow the exact same structure as the existing `AgUiEscalationNotifierTests.cs` and `AgUiEscalationEventSerializationTests.cs`.

### AgUiLearningNotifierTests

```csharp
// File: src/Content/Tests/Presentation.AgentHub.Tests/Notifications/AgUiLearningNotifierTests.cs
// Namespace: Presentation.AgentHub.Tests.Notifications

// Test: AgUiLearningNotifier_LearningCaptured_EmitsSseEvent
//   Arrange: create a LearningEntry with category FactualCorrection, agent scope, source
//   Act: call NotifyLearningCapturedAsync(entry, ct)
//   Assert: writer.WriteAsync called once with a LearningCapturedEvent containing correct
//           learningId, category string, scope (agentId, teamId, isGlobal), sourceDescription

// Test: AgUiLearningNotifier_LearningApplied_EmitsSseEvent
//   Arrange: create a LearningEntry, specify agentId "research-agent"
//   Act: call NotifyLearningAppliedAsync(entry, "research-agent", ct)
//   Assert: writer.WriteAsync called once with a LearningAppliedEvent containing correct
//           learningId, agentId, category string

// Test: AgUiLearningNotifier_NoActiveWriter_NoOp
//   Arrange: set writerAccessor.Writer to null
//   Act: call NotifyLearningCapturedAsync(entry, ct)
//   Assert: writer.WriteAsync never called (Times.Never)

// Test: AgUiLearningNotifier_Exception_LogsWarning_DoesNotThrow
//   Arrange: writer.WriteAsync throws InvalidOperationException("Stream closed")
//   Act: call NotifyLearningCapturedAsync(entry, ct)
//   Assert: act.Should().NotThrowAsync()

// Test: LearningCapturedEvent_SerializesCorrectFields
//   Arrange: construct a LearningCapturedEvent with known values
//   Assert: learningId, category, agentId, teamId, isGlobal, sourceDescription all accessible
//           and match supplied values

// Test: LearningAppliedEvent_IncludesAgentId
//   Arrange: construct a LearningAppliedEvent with agentId "code-writer"
//   Assert: AgentId property equals "code-writer"
```

**Test setup pattern** (mirrors `AgUiEscalationNotifierTests`):

- `Mock<IAgUiEventWriterAccessor>` with `Mock<IAgUiEventWriter>` wired to `.Writer`
- Default `_writerMock.Setup(w => w.WriteAsync(...)).Returns(Task.CompletedTask)`
- SUT constructed with `_accessorMock.Object` and `NullLogger<AgUiLearningNotifier>.Instance`
- For the no-writer test: create a separate SUT with accessor returning null
- For the exception test: reconfigure `_writerMock` to throw, then assert `NotThrowAsync()`
- Helper method `CreateLearningEntry()` returning a sample `LearningEntry` with:
  - `LearningId = Guid.NewGuid()`
  - `Category = LearningCategory.FactualCorrection`
  - `DecayClass = DecayClass.Permanent`
  - `Scope = new LearningScope { AgentId = "test-agent", TeamId = "test-team", IsGlobal = false }`
  - `Content = "Always use ISO 8601 date formats"`
  - `Source = new LearningSource { SourceType = LearningSourceType.HumanCorrection, SourceId = "correction-1", SourceDescription = "User corrected date format" }`
  - `Provenance = new LearningProvenance { OriginPipeline = "chat", OriginTask = "date-formatting", OriginTimestamp = DateTimeOffset.UtcNow, Confidence = 0.95 }`
  - `FeedbackWeight = 1.0`, `UpdateCount = 0`
  - `CreatedAt = DateTimeOffset.UtcNow`

### AgUiLearningEventSerializationTests

```csharp
// File: src/Content/Tests/Presentation.AgentHub.Tests/AgUi/AgUiLearningEventSerializationTests.cs
// Namespace: Presentation.AgentHub.Tests.AgUi

// Test: LearningCapturedEvent_Serializes_WithCorrectTypeDiscriminator
//   Arrange: create LearningCapturedEvent with known values
//   Act: serialize as AgUiEvent using JsonSerializer
//   Assert: JSON contains "type":"LEARNING_CAPTURED", "learningId", "category", "sourceDescription"

// Test: LearningAppliedEvent_Serializes_WithCorrectTypeDiscriminator
//   Arrange: create LearningAppliedEvent with agentId and learningId
//   Act: serialize as AgUiEvent
//   Assert: JSON contains "type":"LEARNING_APPLIED", "agentId", "learningId"

// Test: LearningForgottenEvent_Serializes_WithCorrectTypeDiscriminator
//   Arrange: create LearningForgottenEvent with learningId and reason
//   Act: serialize as AgUiEvent
//   Assert: JSON contains "type":"LEARNING_FORGOTTEN", "learningId", "reason"

// Test: LearningCapturedEvent_Deserializes_BackToCorrectType
//   Arrange: create, serialize, then deserialize as AgUiEvent
//   Assert: result is LearningCapturedEvent with matching field values

// Test: LearningAppliedEvent_WithNullOptionalFields_OmitsThem
//   Arrange: create LearningAppliedEvent with contextSummary = null
//   Act: serialize as AgUiEvent
//   Assert: JSON does not contain "contextSummary"
```

Use the same `JsonSerializerOptions` pattern as `AgUiEscalationEventSerializationTests`:

```csharp
private static readonly JsonSerializerOptions JsonOptions = new()
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
};
```

Serialize using `JsonSerializer.Serialize<AgUiEvent>(evt, JsonOptions)` to trigger polymorphic discriminator emission.

## Files to Create/Modify

| Action | File Path |
|--------|-----------|
| **Create** | `src/Content/Tests/Presentation.AgentHub.Tests/Notifications/AgUiLearningNotifierTests.cs` |
| **Create** | `src/Content/Tests/Presentation.AgentHub.Tests/AgUi/AgUiLearningEventSerializationTests.cs` |
| **Create** | `src/Content/Presentation/Presentation.AgentHub/Notifications/AgUiLearningNotifier.cs` |
| **Modify** | `src/Content/Presentation/Presentation.AgentHub/AgUi/AgUiEventType.cs` -- add 3 learning constants |
| **Modify** | `src/Content/Presentation/Presentation.AgentHub/AgUi/AgUiEvents.cs` -- add 3 `JsonDerivedType` attributes + 3 event record types |

## Implementation Details

### 1. AG-UI Event Type Constants

**File:** `src/Content/Presentation/Presentation.AgentHub/AgUi/AgUiEventType.cs`

Add three new constants to the existing `AgUiEventType` static class (after the drift constants added by section 14):

```csharp
/// <summary>Signals that a new learning has been captured.</summary>
public const string LearningCaptured = "LEARNING_CAPTURED";

/// <summary>Signals that a learning was applied during agent execution.</summary>
public const string LearningApplied = "LEARNING_APPLIED";

/// <summary>Signals that a learning has been forgotten (soft-deleted).</summary>
public const string LearningForgotten = "LEARNING_FORGOTTEN";
```

### 2. AG-UI Learning Event DTOs

**File:** `src/Content/Presentation/Presentation.AgentHub/AgUi/AgUiEvents.cs`

Add three new `JsonDerivedType` attributes to the `AgUiEvent` base record (after the drift attributes added by section 14), then define three event records.

**Polymorphic registration** -- add to the `AgUiEvent` attributes block:

```csharp
[JsonDerivedType(typeof(LearningCapturedEvent), AgUiEventType.LearningCaptured)]
[JsonDerivedType(typeof(LearningAppliedEvent), AgUiEventType.LearningApplied)]
[JsonDerivedType(typeof(LearningForgottenEvent), AgUiEventType.LearningForgotten)]
```

**LearningCapturedEvent** -- emitted when a new learning is saved via `RememberCommand`:

```csharp
/// <summary>
/// Signals that the agent has captured a new learning. Emitted after a
/// <c>RememberCommand</c> successfully persists a <see cref="Domain.AI.Learnings.LearningEntry"/>.
/// </summary>
public sealed record LearningCapturedEvent : AgUiEvent
{
    /// <summary>Unique identifier for the learning entry.</summary>
    [JsonPropertyName("learningId")]
    public required string LearningId { get; init; }

    /// <summary>Learning category (e.g. "FactualCorrection", "StylePreference").</summary>
    [JsonPropertyName("category")]
    public required string Category { get; init; }

    /// <summary>Agent ID this learning is scoped to, if any.</summary>
    [JsonPropertyName("agentId")]
    public string? AgentId { get; init; }

    /// <summary>Team ID this learning is scoped to, if any.</summary>
    [JsonPropertyName("teamId")]
    public string? TeamId { get; init; }

    /// <summary>Whether this is a global learning.</summary>
    [JsonPropertyName("isGlobal")]
    public required bool IsGlobal { get; init; }

    /// <summary>Human-readable description of the learning source.</summary>
    [JsonPropertyName("sourceDescription")]
    public required string SourceDescription { get; init; }
}
```

**LearningAppliedEvent** -- emitted when a learning is recalled and applied to agent context:

```csharp
/// <summary>
/// Signals that a previously captured learning was applied during agent execution.
/// The learning's content influenced the agent's response or tool usage.
/// </summary>
public sealed record LearningAppliedEvent : AgUiEvent
{
    /// <summary>The learning that was applied.</summary>
    [JsonPropertyName("learningId")]
    public required string LearningId { get; init; }

    /// <summary>The agent that applied the learning.</summary>
    [JsonPropertyName("agentId")]
    public required string AgentId { get; init; }

    /// <summary>Learning category for display.</summary>
    [JsonPropertyName("category")]
    public required string Category { get; init; }

    /// <summary>Optional summary of the context in which the learning was applied.</summary>
    [JsonPropertyName("contextSummary")]
    public string? ContextSummary { get; init; }
}
```

**LearningForgottenEvent** -- emitted when a learning is soft-deleted via `ForgetCommand`:

```csharp
/// <summary>
/// Signals that a learning has been forgotten (soft-deleted) and will no longer
/// influence future agent behavior.
/// </summary>
public sealed record LearningForgottenEvent : AgUiEvent
{
    /// <summary>The learning that was forgotten.</summary>
    [JsonPropertyName("learningId")]
    public required string LearningId { get; init; }

    /// <summary>Reason for forgetting this learning.</summary>
    [JsonPropertyName("reason")]
    public required string Reason { get; init; }
}
```

### 3. AgUiLearningNotifier

**File:** `src/Content/Presentation/Presentation.AgentHub/Notifications/AgUiLearningNotifier.cs`

Implements `ILearningNotificationChannel` (from section 06, in `Application.AI.Common.Interfaces.Learnings`). Follow the `AgUiEscalationNotifier` pattern precisely.

**Constructor dependencies:**
- `IAgUiEventWriterAccessor writerAccessor`
- `ILogger<AgUiLearningNotifier> logger`

**`NotifyLearningCapturedAsync(LearningEntry entry, CancellationToken ct)`:**

1. Get writer from `_writerAccessor.Writer`. If null, log debug "No AG-UI writer active; skipping learning-captured event for {LearningId}." and return.
2. Create `LearningCapturedEvent` with:
   - `LearningId` = `entry.LearningId.ToString()`
   - `Category` = `entry.Category.ToString()`
   - `AgentId` = `entry.Scope.AgentId` (nullable, omitted from JSON when null)
   - `TeamId` = `entry.Scope.TeamId` (nullable, omitted from JSON when null)
   - `IsGlobal` = `entry.Scope.IsGlobal`
   - `SourceDescription` = `entry.Source.SourceDescription`
3. Wrap `writer.WriteAsync(evt, ct)` in try/catch: catch `Exception ex when (ex is not OperationCanceledException)`, log warning "Failed to write learning-captured event for {LearningId}."

**`NotifyLearningAppliedAsync(LearningEntry entry, string agentId, CancellationToken ct)`:**

1. Get writer. If null, log debug "No AG-UI writer active; skipping learning-applied event for {LearningId}." and return.
2. Create `LearningAppliedEvent` with:
   - `LearningId` = `entry.LearningId.ToString()`
   - `AgentId` = `agentId`
   - `Category` = `entry.Category.ToString()`
   - `ContextSummary` = null (optional; callers can extend this later)
3. Same try/catch exception handling pattern.

**Class structure:**

```csharp
/// <summary>
/// AG-UI notification channel for learning events. Translates domain learning
/// records into AG-UI SSE events and writes them to the active run's event stream.
/// </summary>
/// <remarks>
/// If no AG-UI run is active (e.g., the learning was captured from ConsoleUI or
/// a background pruning job), the notifier silently skips event emission.
/// </remarks>
public sealed class AgUiLearningNotifier : ILearningNotificationChannel
{
    private readonly IAgUiEventWriterAccessor _writerAccessor;
    private readonly ILogger<AgUiLearningNotifier> _logger;

    /// <summary>
    /// Initializes a new <see cref="AgUiLearningNotifier"/>.
    /// </summary>
    public AgUiLearningNotifier(
        IAgUiEventWriterAccessor writerAccessor,
        ILogger<AgUiLearningNotifier> logger)
    {
        _writerAccessor = writerAccessor;
        _logger = logger;
    }

    // NotifyLearningCapturedAsync and NotifyLearningAppliedAsync as described above
}
```

### 4. ILearningNotificationChannel (dependency from Section 06)

The `ILearningNotificationChannel` interface is defined in section 06 at `src/Content/Application/Application.AI.Common/Interfaces/Learnings/ILearningNotificationChannel.cs`. Its contract:

```csharp
public interface ILearningNotificationChannel
{
    Task NotifyLearningCapturedAsync(LearningEntry entry, CancellationToken ct);
    Task NotifyLearningAppliedAsync(LearningEntry entry, string agentId, CancellationToken ct);
}
```

If section 06 is not yet implemented when this section is built, create a minimal stub interface to unblock compilation. The stub should match the interface signature exactly so it can be replaced in place when section 06 lands.

### 5. DI Registration (handled in Section 18)

The `AgUiLearningNotifier` will be registered in `Presentation.AgentHub/DependencyInjection.cs` as an `ILearningNotificationChannel` Singleton. This registration is covered by section 18 -- this section only creates the implementation and tests.

## Design Notes

**No `LearningForgottenEvent` on the notifier interface:** The `ILearningNotificationChannel` interface (section 06) defines `NotifyLearningCapturedAsync` and `NotifyLearningAppliedAsync` but does not include a `NotifyLearningForgottenAsync` method. The `LearningForgottenEvent` AG-UI DTO exists for future use -- it would be emitted by the `ForgetCommandHandler` (section 13) if a notification channel method is added later. For now, the DTO is defined so the wire format is ready, but no notifier method emits it. This keeps the interface lean while supporting forward-compatible SSE events.

**Graceful no-op pattern:** Identical to `AgUiEscalationNotifier` and `AgUiDriftNotifier`. The notifier checks `_writerAccessor.Writer` for null on every call. This handles:
- ConsoleUI sessions (no SSE stream)
- Background tasks (pruning service, decay calculations)
- Unit tests where no writer is configured

**Exception safety:** All writer calls are wrapped in `try/catch(Exception ex) when (ex is not OperationCanceledException)`. `OperationCanceledException` propagates normally (request cancellation). All other exceptions are logged at Warning level and swallowed -- a failed SSE write must never crash the learning pipeline.

**Event naming on the wire:** Constants follow the existing uppercase-with-underscores AG-UI convention: `LEARNING_CAPTURED`, `LEARNING_APPLIED`, `LEARNING_FORGOTTEN`. These are registered as JSON polymorphic discriminators on the `AgUiEvent` base record so serialization through `JsonSerializer.Serialize<AgUiEvent>(...)` emits the correct `"type"` field.

## Verification

After implementation, run:

```powershell
dotnet build src/Content/Presentation/Presentation.AgentHub/Presentation.AgentHub.csproj
dotnet test src/Content/Tests/Presentation.AgentHub.Tests/Presentation.AgentHub.Tests.csproj --filter "FullyQualifiedName~AgUiLearningNotifier or FullyQualifiedName~AgUiLearningEventSerialization"
```

All 11 tests (6 notifier + 5 serialization) should pass. The notifier depends on `ILearningNotificationChannel` (section 06) and domain types (section 02) being available at compile time. If those sections are not yet implemented, create minimal stub interfaces/records to unblock compilation, or implement this section after sections 02 and 06 are complete.
