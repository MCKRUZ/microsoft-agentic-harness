# Section 18: AG-UI Escalation Events

## Overview

This section adds three new AG-UI event types for escalation notifications (`ESCALATION_REQUESTED`, `ESCALATION_RESOLVED`, `ESCALATION_EXPIRING`) and implements `AgUiEscalationNotifier` -- an `IEscalationNotificationChannel` that pushes escalation lifecycle events to the AG-UI SSE stream. This enables the dashboard to display real-time escalation prompts, approval buttons, and expiry countdowns.

**Layer:** `Presentation.AgentHub`
**Namespaces:** `Presentation.AgentHub.AgUi` (event types), `Presentation.AgentHub.Notifications` (notifier)
**Dependencies:** section-01 (domain escalation types), section-06 (`IEscalationNotificationChannel` interface), section-10 (`CompositeEscalationNotifier` that discovers this channel)
**Blocks:** section-19 (DI registration)

---

## Dependencies

| Dependency | Section | What It Provides |
|------------|---------|-----------------|
| Domain escalation models | section-01 | `EscalationRequest`, `EscalationOutcome`, `EscalationPriority`, `EscalationResolutionType`, `ApproverDecision` |
| Escalation interfaces | section-06 | `IEscalationNotificationChannel` contract (3 notification methods) |
| Notification adapters | section-10 | `CompositeEscalationNotifier` that fans out to all `IEscalationNotificationChannel` registrations |

**Project reference chain:** `Presentation.AgentHub` -> `Presentation.Common` -> `Application.AI.Common` (where `IEscalationNotificationChannel` lives) and -> `Domain.AI` (where escalation records live). No new project references needed.

---

## Files to Create

| File | Project | Description |
|------|---------|-------------|
| `src/Content/Presentation/Presentation.AgentHub/AgUi/AgUiEventType.cs` | Presentation.AgentHub | **Modify** -- add 3 new string constants |
| `src/Content/Presentation/Presentation.AgentHub/AgUi/AgUiEvents.cs` | Presentation.AgentHub | **Modify** -- add 3 new `[JsonDerivedType]` attributes on base class + 3 new sealed record types |
| `src/Content/Presentation/Presentation.AgentHub/AgUi/IAgUiEventWriterAccessor.cs` | Presentation.AgentHub | **Create** -- accessor interface + `AsyncLocal` implementation |
| `src/Content/Presentation/Presentation.AgentHub/Notifications/AgUiEscalationNotifier.cs` | Presentation.AgentHub | **Create** -- `IEscalationNotificationChannel` implementation |
| `src/Content/Presentation/Presentation.AgentHub/AgUi/AgUiRunHandler.cs` | Presentation.AgentHub | **Modify** -- inject accessor, set/clear around execution |
| `src/Content/Tests/Presentation.AgentHub.Tests/AgUi/AgUiEscalationEventSerializationTests.cs` | Presentation.AgentHub.Tests | **Create** -- serialization round-trip tests |
| `src/Content/Tests/Presentation.AgentHub.Tests/Notifications/AgUiEscalationNotifierTests.cs` | Presentation.AgentHub.Tests | **Create** -- notifier behavior tests |

---

## Tests First

### 1. Event Serialization Tests

Create `src/Content/Tests/Presentation.AgentHub.Tests/AgUi/AgUiEscalationEventSerializationTests.cs`.

These tests verify that the new escalation event types serialize with correct JSON polymorphic discriminators, matching the pattern established by the existing `AgUiEventSerializationTests`.

```csharp
/// <summary>
/// Serialization tests for escalation-related AG-UI events.
/// Verifies correct JSON discriminator and property names on the wire.
/// </summary>
public sealed class AgUiEscalationEventSerializationTests
{
    // Test: EscalationRequestedEvent_Serializes_WithCorrectTypeDiscriminator
    //   Arrange: Create EscalationRequestedEvent with escalationId, agentId, toolName,
    //            description, priority ("Critical"), approvers list.
    //   Act: Serialize as AgUiEvent (base type) using JsonOptions.
    //   Assert: JSON contains "type":"ESCALATION_REQUESTED".
    //           JSON contains "escalationId", "agentId", "toolName", "description",
    //           "priority", "approvers" properties with correct values.

    // Test: EscalationResolvedEvent_Serializes_WithCorrectTypeDiscriminator
    //   Arrange: Create EscalationResolvedEvent with escalationId, isApproved=true,
    //            resolutionType ("Approved"), resolvedAt timestamp.
    //   Act: Serialize as AgUiEvent.
    //   Assert: JSON contains "type":"ESCALATION_RESOLVED".

    // Test: EscalationExpiringEvent_Serializes_WithCorrectTypeDiscriminator
    //   Arrange: Create EscalationExpiringEvent with escalationId, remainingSeconds = 30.
    //   Act: Serialize as AgUiEvent.
    //   Assert: JSON contains "type":"ESCALATION_EXPIRING".

    // Test: EscalationRequestedEvent_WithNullOptionalFields_OmitsThem
    //   Arrange: Create EscalationRequestedEvent with arguments=null.
    //   Act: Serialize as AgUiEvent with WhenWritingNull ignore.
    //   Assert: JSON does NOT contain "arguments".

    // Test: EscalationResolvedEvent_Deserializes_BackToCorrectType
    //   Arrange: Create event, serialize as AgUiEvent.
    //   Act: Deserialize the JSON back as AgUiEvent.
    //   Assert: Result is EscalationResolvedEvent with matching property values.
}
```

### 2. Notifier Behavior Tests

Create `src/Content/Tests/Presentation.AgentHub.Tests/Notifications/AgUiEscalationNotifierTests.cs`.

```csharp
/// <summary>
/// Tests for AgUiEscalationNotifier -- verifies correct domain-to-AG-UI
/// event translation and writer invocation.
/// </summary>
public sealed class AgUiEscalationNotifierTests
{
    // Test: NotifyEscalationRequestedAsync_WritesEscalationRequestedEvent
    //   Arrange: Mock IAgUiEventWriterAccessor returns mock IAgUiEventWriter.
    //   Act: Call NotifyEscalationRequestedAsync(request, CancellationToken.None).
    //   Assert: Writer.WriteAsync was called once with an EscalationRequestedEvent.

    // Test: NotifyEscalationResolvedAsync_WritesEscalationResolvedEvent
    //   Arrange: Mock writer. Build EscalationOutcome with IsApproved=true.
    //   Act: Call NotifyEscalationResolvedAsync(outcome, CancellationToken.None).
    //   Assert: Writer.WriteAsync called once with EscalationResolvedEvent.

    // Test: NotifyEscalationExpiringAsync_WritesEscalationExpiringEvent
    //   Arrange: Mock writer. Guid escalationId, TimeSpan remaining = 45 seconds.
    //   Act: Call NotifyEscalationExpiringAsync.
    //   Assert: Writer.WriteAsync called once with EscalationExpiringEvent where RemainingSeconds == 45.

    // Test: NotifyEscalationRequestedAsync_CancellationToken_PassedToWriter
    //   Arrange: Mock writer, create CancellationTokenSource.
    //   Act: Call NotifyEscalationRequestedAsync with the token.
    //   Assert: Writer.WriteAsync received the same CancellationToken.

    // Test: NotifyEscalationRequestedAsync_WriterThrows_PropagatesException
    //   Arrange: Mock writer that throws InvalidOperationException.
    //   Act/Assert: Expect the exception to propagate.
    //   Note: CompositeEscalationNotifier handles per-channel exceptions.
}
```

---

## Implementation Details

### 1. New AG-UI Event Type Constants

**File:** `src/Content/Presentation/Presentation.AgentHub/AgUi/AgUiEventType.cs` (modify)

Add three new constants:

```csharp
/// <summary>Signals that an agent action requires human approval.</summary>
public const string EscalationRequested = "ESCALATION_REQUESTED";

/// <summary>Signals that a pending escalation has been resolved.</summary>
public const string EscalationResolved = "ESCALATION_RESOLVED";

/// <summary>Warns that a pending escalation is approaching its timeout deadline.</summary>
public const string EscalationExpiring = "ESCALATION_EXPIRING";
```

### 2. New AG-UI Event Records

**File:** `src/Content/Presentation/Presentation.AgentHub/AgUi/AgUiEvents.cs` (modify)

Add three `[JsonDerivedType]` attributes to `AgUiEvent` base record and three new sealed record types.

**Base class additions:**

```csharp
[JsonDerivedType(typeof(EscalationRequestedEvent), AgUiEventType.EscalationRequested)]
[JsonDerivedType(typeof(EscalationResolvedEvent), AgUiEventType.EscalationResolved)]
[JsonDerivedType(typeof(EscalationExpiringEvent), AgUiEventType.EscalationExpiring)]
```

**New record types:**

`EscalationRequestedEvent`:
- `EscalationId` (string) -- `Guid.ToString()` of domain `EscalationRequest.EscalationId`
- `AgentId` (string)
- `ToolName` (string)
- `Description` (string)
- `Priority` (string) -- stringified `EscalationPriority` enum value
- `Approvers` (IReadOnlyList<string>)
- `TimeoutSeconds` (int)
- `Arguments` (IReadOnlyDictionary<string, object>?, nullable)

`EscalationResolvedEvent`:
- `EscalationId` (string)
- `IsApproved` (bool)
- `ResolutionType` (string) -- stringified `EscalationResolutionType`
- `ResolvedAt` (DateTimeOffset)
- `Decisions` (IReadOnlyList<AgUiApproverDecision>?, nullable)

`AgUiApproverDecision` -- lightweight nested record for wire serialization:
- `ApproverName` (string)
- `Approved` (bool)
- `Reason` (string?, nullable)

`EscalationExpiringEvent`:
- `EscalationId` (string)
- `RemainingSeconds` (int)

### 3. IAgUiEventWriterAccessor

**File:** `src/Content/Presentation/Presentation.AgentHub/AgUi/IAgUiEventWriterAccessor.cs` (create)

```csharp
/// <summary>
/// Provides access to the current AG-UI event writer for the active run.
/// Uses <see cref="AsyncLocal{T}"/> storage so the writer is scoped to the
/// async execution context of the AG-UI run handler.
/// </summary>
public interface IAgUiEventWriterAccessor
{
    /// <summary>Gets or sets the current AG-UI event writer. Null when no run is active.</summary>
    IAgUiEventWriter? Writer { get; set; }
}

/// <summary>
/// Default implementation using <see cref="AsyncLocal{T}"/> for execution-context-scoped storage.
/// </summary>
public sealed class AgUiEventWriterAccessor : IAgUiEventWriterAccessor
{
    private static readonly AsyncLocal<IAgUiEventWriter?> _current = new();

    /// <inheritdoc />
    public IAgUiEventWriter? Writer
    {
        get => _current.Value;
        set => _current.Value = value;
    }
}
```

### 4. AgUiRunHandler Modification

**File:** `src/Content/Presentation/Presentation.AgentHub/AgUi/AgUiRunHandler.cs` (modify)

- Add `IAgUiEventWriterAccessor` to constructor injection
- In `HandleRunAsync`, after creating the `AgUiEventWriter`, set `_writerAccessor.Writer = writer`
- In the `finally` block: `_writerAccessor.Writer = null`

### 5. AgUiEscalationNotifier

**File:** `src/Content/Presentation/Presentation.AgentHub/Notifications/AgUiEscalationNotifier.cs` (create)

```csharp
/// <summary>
/// AG-UI notification channel for escalation events. Translates domain escalation
/// records into AG-UI SSE events and writes them to the active run's event stream.
/// </summary>
/// <remarks>
/// If no AG-UI run is active (i.e., the escalation was triggered from the ConsoleUI
/// or a non-SSE context), the notifier silently skips event emission. This is by design --
/// escalation notifications also flow through other channels (Slack, Teams).
/// </remarks>
public sealed class AgUiEscalationNotifier : IEscalationNotificationChannel
{
    // Constructor: IAgUiEventWriterAccessor writerAccessor, ILogger<AgUiEscalationNotifier> logger

    // NotifyEscalationRequestedAsync: Get writer, null-check, construct EscalationRequestedEvent, write.
    // NotifyEscalationResolvedAsync: Get writer, null-check, map decisions, construct EscalationResolvedEvent, write.
    // NotifyEscalationExpiringAsync: Get writer, null-check, construct EscalationExpiringEvent, write.
}
```

**Key behaviors:**
- **Null writer is normal, not exceptional.** ConsoleUI host does not have AG-UI SSE streams. Log at `Debug` and return.
- **No exception swallowing.** If `writer.WriteAsync` throws, the exception propagates to `CompositeEscalationNotifier` which catches and logs.
- **String conversion of enums.** `ToString()` on C# enums produces the member name (e.g., `"Critical"`, `"Approved"`).

---

## Wire Format Examples

**ESCALATION_REQUESTED:**
```json
{
  "type": "ESCALATION_REQUESTED",
  "escalationId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "agentId": "research-agent",
  "toolName": "file_system_write",
  "description": "Agent attempted to write to protected directory /etc/config",
  "priority": "Critical",
  "approvers": ["admin@company.com", "security-team@company.com"],
  "timeoutSeconds": 300,
  "arguments": { "path": "/etc/config/app.yaml", "content": "..." }
}
```

**ESCALATION_RESOLVED:**
```json
{
  "type": "ESCALATION_RESOLVED",
  "escalationId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "isApproved": true,
  "resolutionType": "Approved",
  "resolvedAt": "2026-05-08T14:30:00Z",
  "decisions": [
    { "approverName": "admin@company.com", "approved": true, "reason": "Verified safe" }
  ]
}
```

**ESCALATION_EXPIRING:**
```json
{
  "type": "ESCALATION_EXPIRING",
  "escalationId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "remainingSeconds": 30
}
```

---

## DI Registration (section-19 responsibility)

```csharp
// In Presentation.AgentHub/DependencyInjection.cs:
services.AddSingleton<IAgUiEventWriterAccessor, AgUiEventWriterAccessor>();
services.AddSingleton<IEscalationNotificationChannel, AgUiEscalationNotifier>();
```

---

## File Checklist

| File | Action | Project |
|------|--------|---------|
| `src/Content/Presentation/Presentation.AgentHub/AgUi/AgUiEventType.cs` | Modify (add 3 constants) | Presentation.AgentHub |
| `src/Content/Presentation/Presentation.AgentHub/AgUi/AgUiEvents.cs` | Modify (add 3 `[JsonDerivedType]` attrs, 3 event records, 1 helper record) | Presentation.AgentHub |
| `src/Content/Presentation/Presentation.AgentHub/AgUi/IAgUiEventWriterAccessor.cs` | Create | Presentation.AgentHub |
| `src/Content/Presentation/Presentation.AgentHub/Notifications/AgUiEscalationNotifier.cs` | Create | Presentation.AgentHub |
| `src/Content/Presentation/Presentation.AgentHub/AgUi/AgUiRunHandler.cs` | Modify (inject accessor, set/clear around execution) | Presentation.AgentHub |
| `src/Content/Tests/Presentation.AgentHub.Tests/AgUi/AgUiEscalationEventSerializationTests.cs` | Create | Presentation.AgentHub.Tests |
| `src/Content/Tests/Presentation.AgentHub.Tests/Notifications/AgUiEscalationNotifierTests.cs` | Create | Presentation.AgentHub.Tests |

---

## Verification

```
dotnet build src/Content/Presentation/Presentation.AgentHub/Presentation.AgentHub.csproj
dotnet test src/Content/Tests/Presentation.AgentHub.Tests/Presentation.AgentHub.Tests.csproj --filter "FullyQualifiedName~AgUiEscalationEvent|FullyQualifiedName~AgUiEscalationNotifier"
```

---

## Implementation Notes (Post-Implementation)

### Deviations from Plan

1. **Arguments type changed to `IReadOnlyDictionary<string, string>?`** — Plan specified `IReadOnlyDictionary<string, object>?`. Changed to match the domain model exactly, avoiding unnecessary boxing and fragile `System.Text.Json` runtime type detection.

2. **Exception handling added to notifier** — Plan showed exceptions propagating. Code review identified this violates the `IEscalationNotificationChannel` contract ("MUST NOT throw"). All three methods now wrap `writer.WriteAsync` in try-catch, logging at Warning. `OperationCanceledException` still propagates.

3. **RemainingSeconds clamped** — Added `Math.Max(0, ...)` to prevent negative values when timer expiry races with notification delivery.

4. **Writer accessor set inside try block** — Moved from before try to inside try for proper cleanup guarantee.

5. **AgUiRunHandlerTests updated** — Existing test helper `BuildHandler` updated to pass new `AgUiEventWriterAccessor` parameter.

### Test Counts
- Serialization: 7 tests (5 original + 2 round-trip deserialization)
- Notifier: 7 tests (5 original + negative-clamp + catch-and-log)
- RunHandler regression: 7 existing tests pass unchanged
