# Section 05: OTel → SignalR Bridge

## Overview

This section implements `SignalRSpanExporter` — a custom OpenTelemetry exporter that bridges the OTel Activity pipeline to SignalR clients in real time. Every span emitted by the agent pipeline flows to connected WebUI clients, enabling live telemetry visualization.

**Project:** `Presentation.AgentHub`
**Depends on:** section-02-agenthub-core (DI wiring, SignalR registered, `AgentTelemetryHub` exists)
**Can parallelize with:** section-03-conversation-store, section-06-mcp-api, section-09-msal-auth

---

## Tests First

Write these tests before implementing. Test class: `SignalRSpanExporterTests` in `Presentation.AgentHub.Tests`.

### Channel and Export Tests

```csharp
// Test: Export with full channel (capacity exceeded) does not block
// Arrange: fill the channel to capacity (1000 items) with a mock writer that always returns false
// Act: call Export() with one more batch item, measure elapsed time
// Assert: elapsed < 1ms (TryWrite returns immediately)

// Test: Export logs a warning when channel is full and a span is dropped
// Arrange: mock ILogger<SignalRSpanExporter>, fill channel to capacity
// Act: Export() one more span
// Assert: logger received a Warning-level call containing "dropped" or "full"

// Test: MapToSpanData sets ParentSpanId to null for root spans
// Arrange: create Activity with no parent (ParentSpanId == default(ActivitySpanId))
// Act: call the private MapToSpanData via reflection, or expose as internal + [InternalsVisibleTo]
// Assert: result.ParentSpanId is null

// Test: MapToSpanData extracts agent.conversation_id tag into ConversationId field
// Arrange: Activity with tag "agent.conversation_id" = "conv-abc"
// Act: MapToSpanData
// Assert: result.ConversationId == "conv-abc"

// Test: MapToSpanData sets ConversationId to null when tag is absent
// Arrange: Activity with no "agent.conversation_id" tag
// Act: MapToSpanData
// Assert: result.ConversationId is null
```

### Drain Loop Tests

```csharp
// Test: Span with ConversationId is sent to conversation:{conversationId} group
// Arrange: mock IHubContext<AgentTelemetryHub>, write SpanData with ConversationId="conv-1" to channel
// Act: start the hosted service, allow drain loop one iteration
// Assert: hubContext.Clients.Group("conversation:conv-1").SendAsync("SpanReceived", ...) was called

// Test: Span is always sent to global-traces group regardless of ConversationId
// Arrange: SpanData with ConversationId set AND SpanData with ConversationId=null
// Act: drain loop processes both
// Assert: both called hubContext.Clients.Group("global-traces").SendAsync("SpanReceived", ...)

// Test: StopAsync completes the channel and drain loop exits cleanly
// Arrange: running hosted service with empty channel
// Act: StopAsync(CancellationToken.None)
// Assert: drain loop task completes within reasonable timeout (e.g. 500ms), no exception thrown
```

**Test verification command:** `dotnet test src/AgenticHarness.slnx`

---

## Files to Create

```
src/Content/Presentation/Presentation.AgentHub/
  Telemetry/
    SpanData.cs
    SignalRSpanExporter.cs
```

The `SignalRSpanExporter` class also needs a registration hook in the existing:

```
src/Content/Presentation/Presentation.AgentHub/
  DependencyInjection.cs   ← modify (add exporter registration)
```

---

## SpanData Record

Define in `Telemetry/SpanData.cs`. This record is the contract between the .NET exporter and the TypeScript frontend — field names map directly to the TypeScript `SpanData` interface in section-11.

```csharp
/// <summary>
/// Immutable snapshot of an OpenTelemetry span, serialized over SignalR to connected WebUI clients.
/// All fields needed by the frontend trace tree renderer.
/// </summary>
public record SpanData(
    string Name,
    string TraceId,
    string SpanId,
    string? ParentSpanId,           // null for root spans (no parent)
    string? ConversationId,         // from agent.conversation_id activity tag; null for non-agent spans
    DateTimeOffset StartTime,
    double DurationMs,
    string Status,                  // "unset" | "ok" | "error"
    string? StatusDescription,
    string Kind,                    // "internal" | "client" | "server"
    string SourceName,
    IReadOnlyDictionary<string, string> Tags
);
```

`ParentSpanId` must be `null` (not `default` string) for root spans — the TypeScript frontend uses `parentSpanId === null` to identify tree roots.

---

## SignalRSpanExporter Design

Define in `Telemetry/SignalRSpanExporter.cs`. The class must implement both `BaseExporter<Activity>` and `IHostedService`.

### Why this dual-interface pattern

`BaseExporter<Activity>.Export()` is called synchronously from the OTel SDK background thread. `await` is not available, and calling SignalR's `SendAsync` directly would block the OTel pipeline under load. The solution: `Export()` writes span data to a bounded `Channel<SpanData>` non-blocking via `TryWrite`. `IHostedService.StartAsync` drains the channel asynchronously in a background loop and calls `SendAsync`. The bounded channel with `DropOldest` ensures backpressure never blocks the export pipeline — spans are dropped with a warning rather than stalling the agent.

### Constructor

Takes `IHubContext<AgentTelemetryHub>` and `ILogger<SignalRSpanExporter>` via DI. Creates the channel in the constructor:

```csharp
Channel.CreateBounded<SpanData>(new BoundedChannelOptions(1000)
{
    FullMode = BoundedChannelFullMode.DropOldest
});
```

### Export method stub

```csharp
/// <summary>
/// Called synchronously by the OTel SDK. Writes each span to the bounded channel.
/// Never blocks — if the channel is full, the oldest span is dropped and a warning is logged.
/// </summary>
public override ExportResult Export(in Batch<Activity> batch)
{
    // iterate batch
    // call MapToSpanData(activity)
    // TryWrite to channel; if false, log Warning "OTel channel full — span dropped: {SpanName}"
    // always return ExportResult.Success
}
```

### MapToSpanData helper stub

```csharp
/// <summary>
/// Converts an Activity to a SpanData record. Extracts agent.conversation_id tag into ConversationId.
/// Sets ParentSpanId to null when activity.ParentSpanId == default(ActivitySpanId).
/// </summary>
private static SpanData MapToSpanData(Activity activity)
{
    // ParentSpanId: activity.ParentSpanId == default(ActivitySpanId) ? null : activity.ParentSpanId.ToHexString()
    // ConversationId: activity.GetTagItem("agent.conversation_id") as string
    // Status: map activity.Status to "unset" | "ok" | "error"
    // Kind: map activity.Kind to "internal" | "client" | "server"
    // Tags: activity.Tags as IReadOnlyDictionary<string, string>
    // DurationMs: activity.Duration.TotalMilliseconds
}
```

### IHostedService methods

```csharp
/// <summary>
/// Starts the background drain loop that reads from the channel and broadcasts spans via SignalR.
/// Uses await Task.WhenAll(...) per span — NOT Task.Run — to preserve ordering and propagate exceptions.
/// </summary>
public Task StartAsync(CancellationToken cancellationToken)
{
    // _drainTask = DrainAsync(cancellationToken);
    // return Task.CompletedTask;
}

/// <summary>
/// Completes the channel writer so ReadAllAsync terminates, then awaits the drain task.
/// </summary>
public async Task StopAsync(CancellationToken cancellationToken)
{
    // _channel.Writer.Complete();
    // await _drainTask;
}
```

### DrainAsync loop

```csharp
private async Task DrainAsync(CancellationToken ct)
{
    await foreach (var span in _channel.Reader.ReadAllAsync(ct))
    {
        // Build tasks list
        // If span.ConversationId != null: add SendAsync to "conversation:{span.ConversationId}" group
        // Always add SendAsync to "global-traces" group
        // await Task.WhenAll(tasks)
        // Do NOT use Task.Run — all awaits happen inline in this loop
    }
}
```

The `await Task.WhenAll(...)` pattern (not `Task.Run`, not fire-and-forget) is required to:
- Preserve span ordering per group
- Surface exceptions (rather than swallowing them)
- Avoid GC pressure from unbounded task spawning

The `ConversationId` routing key comes from the `agent.conversation_id` Activity tag set by `AgentTelemetryHub` before dispatching `ExecuteAgentTurnCommand` (section-04). Do not use `TraceId` as the group key — it is a different identifier.

---

## Registration

In `DependencyInjection.cs`, inside `AddAgentHubServices`, after SignalR is registered:

```csharp
services.AddSingleton<SignalRSpanExporter>();
services.AddHostedService(sp => sp.GetRequiredService<SignalRSpanExporter>());
```

The exporter must be added to the OTel tracing pipeline. Because `Infrastructure.Observability` registers its `ITelemetryConfigurator` at order 300 and must run last, add the exporter via an explicit `.WithTracing(...)` call in `AddAgentHubServices` after `GetServices()` has run:

```csharp
services.AddOpenTelemetry()
    .WithTracing(b => b.AddExporter(
        sp => sp.GetRequiredService<SignalRSpanExporter>()));
```

This appends the `SignalRSpanExporter` after the standard exporters (Jaeger, Azure Monitor) already registered by `Infrastructure.Observability`, without touching that layer's DI code.

---

## SignalR Group Naming Convention

| Group name | Who joins | What they receive |
|---|---|---|
| `conversation:{conversationId}` | Client calls `JoinConversationGroup(conversationId)` | Spans tagged with that conversation's `agent.conversation_id` |
| `global-traces` | Client calls `JoinGlobalTraces()` (requires `AgentHub.Traces.ReadAll` role — enforced in section-04) | All spans regardless of conversation |

Every span is sent to `global-traces`. Only spans with a non-null `ConversationId` are also sent to the conversation group.

---

## Key Implementation Constraints

- `Export()` must never `await` — it runs on the OTel SDK background thread.
- `Export()` must always return `ExportResult.Success` — even when items are dropped.
- The drain loop must use `await Task.WhenAll(...)` inline, not `Task.Run` per span.
- Channel capacity is exactly 1000 with `DropOldest` — not `Wait`, not `ThrowIfFull`.
- `MapToSpanData` must set `ParentSpanId = null` (not `default` string) when `activity.ParentSpanId == default(ActivitySpanId)`.
- `ConversationId` comes from the `agent.conversation_id` Activity tag, not from `TraceId`.

---

## As Built

### Files created
- `Telemetry/SpanData.cs` — public record, all fields as specified
- `Telemetry/SignalRSpanExporter.cs` — `BaseExporter<Activity>` + `IHostedService`; `MapToSpanData` is `internal static`

### Files modified
- `DependencyInjection.cs` — exporter singleton + hosted service + OTel pipeline appended via `AddProcessor`
- `Presentation.AgentHub.csproj` — added `OpenTelemetry.Extensions.Hosting`; `InternalsVisibleTo("Presentation.AgentHub.Tests")`
- `Hubs/AgentTelemetryHub.cs` — `ConversationGroup` and `GlobalTracesGroup` changed `private` → `internal`

### Deviations from plan
- **DI registration**: spec showed `b.AddExporter(factory)`. Used `b.AddProcessor(factory)` with a
  file-scoped `AgentHubSpanExportProcessor : SimpleExportProcessor<Activity>` instead, because
  `SimpleExportProcessor<T>` is abstract in OTel 1.12.0 (cannot be `new`'d directly) and `AddExporter`
  wraps in a processor internally anyway.
- **Hub string constants**: `ConversationGroup`/`GlobalTracesGroup` made `internal` (code review fix)
  so `SignalRSpanExporter` uses them directly instead of duplicating string literals.
- **Drain loop exception handling**: added `try/catch` per span in `DrainAsync` to prevent a single
  SignalR failure from silently terminating the drain loop (code review fix).

### Test processor pattern
`SimpleExportProcessor<T>` is abstract; tests use a private `TestExportProcessor : SimpleExportProcessor<Activity>`
inner class (trivial subclass, no overrides needed) to call `processor.OnEnd(activity)` → `Export(batch)`.

### Tests
9 tests in `Telemetry/SignalRSpanExporterTests.cs` — all passing.
Total `Presentation.AgentHub.Tests`: 44/44.
