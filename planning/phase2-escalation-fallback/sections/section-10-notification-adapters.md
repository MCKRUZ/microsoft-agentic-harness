# Section 10: Notification Adapters

## Overview

This section implements the notification delivery layer for the escalation subsystem: `CompositeEscalationNotifier` (the fan-out orchestrator), `NoOpSlackNotifier`, and `NoOpTeamsNotifier`. These are the Infrastructure.AI implementations of the interfaces defined in section-06 (`IEscalationNotifier` and `IEscalationNotificationChannel`).

The composite pattern ensures `DefaultEscalationService` (section-08) calls a single `IEscalationNotifier`, which transparently fans out to every registered `IEscalationNotificationChannel`. Adding a new delivery channel (Slack, Teams, email, PagerDuty) is just a DI registration -- no code changes to the service or composite.

**Layer:** `Infrastructure.AI`
**Namespace:** `Infrastructure.AI.Escalation`
**Dependencies:** section-01 (domain escalation types), section-06 (IEscalationNotifier, IEscalationNotificationChannel interfaces)
**Blocks:** section-18 (AG-UI escalation notifier, which is another `IEscalationNotificationChannel` registered in the Presentation layer), section-19 (DI registration)

---

## Dependencies

| Dependency | Section | What It Provides |
|------------|---------|-----------------|
| Domain escalation models | section-01 | `EscalationRequest`, `EscalationOutcome` records, `Guid` escalation IDs |
| Escalation interfaces | section-06 | `IEscalationNotifier`, `IEscalationNotificationChannel` contracts |

No external NuGet packages are needed. These classes depend only on `Microsoft.Extensions.Logging` (already referenced by `Infrastructure.AI`).

---

## Files to Create

All files go in `src/Content/Infrastructure/Infrastructure.AI/Escalation/`:

| File | Type | Description |
|------|------|-------------|
| `CompositeEscalationNotifier.cs` | class | Implements `IEscalationNotifier`, fans out to all `IEscalationNotificationChannel` instances |
| `NoOpSlackNotifier.cs` | class | Implements `IEscalationNotificationChannel`, logs at Debug level, does nothing |
| `NoOpTeamsNotifier.cs` | class | Implements `IEscalationNotificationChannel`, logs at Debug level, does nothing |

Test file: `src/Content/Tests/Infrastructure.AI.Tests/Escalation/CompositeEscalationNotifierTests.cs`

---

## Tests First

Create `src/Content/Tests/Infrastructure.AI.Tests/Escalation/CompositeEscalationNotifierTests.cs`.

The test class verifies the composite fan-out behavior, error isolation, and edge cases. Uses xUnit + Moq + FluentAssertions, consistent with the existing test patterns in `Infrastructure.AI.Tests` (see `CompositeHookExecutorTests` and `CompositeStateManagerTests` for style reference).

```csharp
// File: src/Content/Tests/Infrastructure.AI.Tests/Escalation/CompositeEscalationNotifierTests.cs
// Namespace: Infrastructure.AI.Tests.Escalation
// Using: Domain.AI.Escalation, Application.AI.Common.Interfaces.Escalation,
//        Infrastructure.AI.Escalation, FluentAssertions, Moq, Xunit,
//        Microsoft.Extensions.Logging

/// <summary>
/// Tests for <see cref="CompositeEscalationNotifier"/> fan-out behavior.
/// Verifies that notifications reach all registered channels and that
/// individual channel failures do not block other channels.
/// </summary>
public sealed class CompositeEscalationNotifierTests
{
    // --- Fan-out behavior ---

    // Test: NotifyEscalationRequestedAsync_FansOutToAllChannels
    //   Arrange: Create 3 mock IEscalationNotificationChannel instances.
    //            Construct CompositeEscalationNotifier with the 3 channels.
    //            Build a valid EscalationRequest.
    //   Act: Call NotifyEscalationRequestedAsync.
    //   Assert: Each mock channel's NotifyEscalationRequestedAsync was called
    //           exactly once with the same request.

    // Test: NotifyEscalationResolvedAsync_FansOutToAllChannels
    //   Arrange: 2 mock channels, a valid EscalationOutcome.
    //   Act: Call NotifyEscalationResolvedAsync.
    //   Assert: Both channels received the outcome.

    // Test: NotifyEscalationExpiringAsync_FansOutToAllChannels
    //   Arrange: 2 mock channels, escalation ID, TimeSpan remaining.
    //   Act: Call NotifyEscalationExpiringAsync.
    //   Assert: Both channels received the escalation ID and remaining time.

    // --- Error isolation ---

    // Test: NotifyEscalationRequestedAsync_ChannelFailure_DoesNotBlockOthers
    //   Arrange: 3 mock channels. Channel 2 throws InvalidOperationException
    //            from NotifyEscalationRequestedAsync.
    //   Act: Call NotifyEscalationRequestedAsync.
    //   Assert: Channels 1 and 3 were still called. No exception propagates to caller.

    // Test: NotifyEscalationRequestedAsync_ChannelFailure_LogsWarning
    //   Arrange: 1 mock channel that throws. Use a mock ILogger<CompositeEscalationNotifier>.
    //   Act: Call NotifyEscalationRequestedAsync.
    //   Assert: Logger received a Warning-level log entry.
    //   Note: Verifying log calls with Moq requires checking LogLevel on the
    //         ILogger.Log<TState> generic method. Match on LogLevel.Warning.

    // --- Edge cases ---

    // Test: NoChannelsRegistered_CompletesSuccessfully
    //   Arrange: Empty IEnumerable<IEscalationNotificationChannel>.
    //   Act: Call all three notification methods.
    //   Assert: All complete without exception.
}
```

**Test helpers:** Build `EscalationRequest` and `EscalationOutcome` test fixtures using required properties from section-01 domain types. A private helper method in the test class keeps Arrange blocks clean:

```csharp
// Helper: CreateTestRequest() -> EscalationRequest with minimal valid properties
// Helper: CreateTestOutcome(Guid escalationId) -> EscalationOutcome with Approved resolution
```

---

## Implementation Details

### 1. CompositeEscalationNotifier

**File:** `src/Content/Infrastructure/Infrastructure.AI/Escalation/CompositeEscalationNotifier.cs`

**Namespace:** `Infrastructure.AI.Escalation`

The composite implements `IEscalationNotifier` and injects `IEnumerable<IEscalationNotificationChannel>`. It fans out each notification to all channels concurrently using `Task.WhenAll`, catching and logging per-channel failures without letting them propagate.

Key design decisions:

- **Concurrent fan-out:** All channels are notified in parallel (`Task.WhenAll` over wrapped per-channel tasks). A slow Slack adapter does not delay the AG-UI adapter.
- **Error isolation:** Each channel call is wrapped in a try-catch. Failures are logged at `Warning` level with the channel type name and exception. The composite itself never throws due to a channel failure.
- **No infinite recursion:** The composite implements `IEscalationNotifier` and injects `IEnumerable<IEscalationNotificationChannel>` -- these are different interfaces, so the composite never receives itself via DI. This is the explicit reason for the two-interface split in section-06.

```csharp
/// <summary>
/// Fans out escalation notifications to all registered <see cref="IEscalationNotificationChannel"/> instances.
/// Individual channel failures are caught and logged without blocking other channels.
/// </summary>
/// <remarks>
/// Registered as the single <see cref="IEscalationNotifier"/> implementation.
/// To add a new delivery channel, implement <see cref="IEscalationNotificationChannel"/>
/// and register it in DI -- the composite discovers channels automatically.
/// </remarks>
public sealed class CompositeEscalationNotifier : IEscalationNotifier
{
    // Constructor: IEnumerable<IEscalationNotificationChannel> channels, ILogger<CompositeEscalationNotifier> logger
    // Store as IReadOnlyList<IEscalationNotificationChannel> (_channels) for indexed access in logging.

    // NotifyEscalationRequestedAsync: Fan out request to all channels.
    //   - Build Task[] from _channels.Select(c => SafeNotifyAsync(() => c.NotifyEscalationRequestedAsync(request, ct), c))
    //   - await Task.WhenAll(tasks)

    // NotifyEscalationResolvedAsync: Same fan-out pattern with outcome.

    // NotifyEscalationExpiringAsync: Same fan-out pattern with escalationId + remaining.

    // Private SafeNotifyAsync(Func<Task> action, IEscalationNotificationChannel channel):
    //   try { await action(); }
    //   catch (Exception ex) { _logger.LogWarning(ex, "Notification channel {Channel} failed", channel.GetType().Name); }
}
```

The `SafeNotifyAsync` private method encapsulates the try-catch-log pattern so each notification method stays clean and DRY.

### 2. NoOpSlackNotifier

**File:** `src/Content/Infrastructure/Infrastructure.AI/Escalation/NoOpSlackNotifier.cs`

**Namespace:** `Infrastructure.AI.Escalation`

A placeholder extension point for Slack integration. Logs at `Debug` level that a notification would be sent, then returns `Task.CompletedTask`. Template consumers replace this with a real Slack SDK adapter.

```csharp
/// <summary>
/// No-op Slack notification channel. Logs escalation events at Debug level
/// without delivering them. Replace with a real Slack SDK adapter for production use.
/// </summary>
/// <remarks>
/// Registered as an <see cref="IEscalationNotificationChannel"/> entry in DI.
/// The <see cref="CompositeEscalationNotifier"/> automatically discovers and
/// includes this channel in fan-out notifications.
/// </remarks>
public sealed class NoOpSlackNotifier : IEscalationNotificationChannel
{
    // Constructor: ILogger<NoOpSlackNotifier> logger

    // All three methods:
    //   _logger.LogDebug("Slack: would notify escalation {Event} for {EscalationId}", ...);
    //   return Task.CompletedTask;
}
```

### 3. NoOpTeamsNotifier

**File:** `src/Content/Infrastructure/Infrastructure.AI/Escalation/NoOpTeamsNotifier.cs`

**Namespace:** `Infrastructure.AI.Escalation`

Identical pattern to `NoOpSlackNotifier` but for Microsoft Teams. Separate class so template consumers can replace one without affecting the other, and so each has its own DI registration as a distinct `IEscalationNotificationChannel` entry.

```csharp
/// <summary>
/// No-op Microsoft Teams notification channel. Logs escalation events at Debug level
/// without delivering them. Replace with a real Teams webhook or Graph API adapter
/// for production use.
/// </summary>
public sealed class NoOpTeamsNotifier : IEscalationNotificationChannel
{
    // Constructor: ILogger<NoOpTeamsNotifier> logger
    // Same pattern as NoOpSlackNotifier with "Teams:" prefix in log messages.
}
```

---

## DI Registration (section-19 responsibility, noted for context)

Registration happens in section-19. The expected pattern:

```csharp
// In Infrastructure.AI/DependencyInjection.cs:
services.AddSingleton<IEscalationNotifier, CompositeEscalationNotifier>();
services.AddSingleton<IEscalationNotificationChannel, NoOpSlackNotifier>();
services.AddSingleton<IEscalationNotificationChannel, NoOpTeamsNotifier>();

// In Presentation.AgentHub (section-18):
services.AddSingleton<IEscalationNotificationChannel, AgUiEscalationNotifier>();
```

The composite resolves `IEnumerable<IEscalationNotificationChannel>` from DI, which includes all registered channel implementations. The composite itself is registered as `IEscalationNotifier` (not as `IEscalationNotificationChannel`), preventing circular injection.

---

## Conventions to Follow

These implementations follow patterns already established in the codebase:

- **Composite pattern:** Same approach as `CompositeHookExecutor` and `CompositeStateManager` -- inject `IEnumerable<T>`, fan out, isolate failures.
- **Error isolation:** `CompositeHookExecutor` wraps each hook in try-catch and logs failures without propagating. The notifier follows the same pattern.
- **No-op stubs:** The project uses no-op/structured-log implementations for extension points (e.g., `StructuredLogContentSafetyService`, `StructuredLogAuditSink`). The no-op notifiers follow this same convention.
- **Sealed classes:** All concrete implementations are `sealed` (matches `JsonlDelegationStore`, `CapabilityMatchSupervisor`, etc.).
- **ILogger injection:** Every class takes `ILogger<T>` via constructor injection. Log levels: `Debug` for no-op stubs, `Warning` for channel failures in the composite.
- **XML docs:** Full documentation on all public types. `<remarks>` on each class explains the extension point and replacement path. This is a template -- docs are teaching material.
- **Namespace:** `Infrastructure.AI.Escalation` -- parallel to `Infrastructure.AI.Agents`, `Infrastructure.AI.Governance`, etc.
- **One class per file:** Consistent with project conventions.

---

## File Checklist

| File | Action | Project |
|------|--------|---------|
| `src/Content/Infrastructure/Infrastructure.AI/Escalation/CompositeEscalationNotifier.cs` | Create | Infrastructure.AI |
| `src/Content/Infrastructure/Infrastructure.AI/Escalation/NoOpSlackNotifier.cs` | Create | Infrastructure.AI |
| `src/Content/Infrastructure/Infrastructure.AI/Escalation/NoOpTeamsNotifier.cs` | Create | Infrastructure.AI |
| `src/Content/Tests/Infrastructure.AI.Tests/Escalation/CompositeEscalationNotifierTests.cs` | Create | Infrastructure.AI.Tests |

---

## Verification

After creating all files, run:

```
dotnet build src/Content/Infrastructure/Infrastructure.AI/Infrastructure.AI.csproj
dotnet test src/Content/Tests/Infrastructure.AI.Tests/Infrastructure.AI.Tests.csproj --filter "FullyQualifiedName~Infrastructure.AI.Tests.Escalation.CompositeEscalationNotifier"
```

The build requires section-01 domain types and section-06 interfaces to exist. All 8 tests should pass. No new NuGet dependencies are needed.

---

## Implementation Notes

**Deviations from plan:**
- Interface signature uses `EscalationRequest` (not bare `Guid escalationId`) for `NotifyEscalationExpiringAsync` — matches the actual interface defined in section-06.
- Added 2 tests beyond original spec during code review:
  - `NotifyEscalationRequestedAsync_AllChannelsFail_CompletesWithoutException` — documents guarantee that composite never throws.
  - `NotifyEscalationRequestedAsync_ForwardsCancellationToken` — verifies exact token propagation to channels.

**Final test count:** 8 (original 6 + 2 from review)
