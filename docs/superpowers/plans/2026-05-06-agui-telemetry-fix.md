# AG-UI Session Telemetry Fix

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the AG-UI path so session telemetry (turns, tokens, tool calls, cost) is persisted to the observability store, making the dashboard show real data instead of zeros.

**Architecture:** Store the `ObservabilitySessionId` and a `TelemetryAccumulator` on the `ConversationRecord` so the stateless AG-UI HTTP handler can carry session state across requests. After each turn, accumulate metrics and call `UpdateSessionMetricsAsync` — mirroring what the SignalR hub already does via its in-memory `ActiveConversationInfo`.

**Tech Stack:** C# .NET 10, xUnit, Moq, FluentAssertions

---

### Task 1: Create TelemetryAccumulator record

**Files:**
- Create: `src/Content/Presentation/Presentation.AgentHub/DTOs/TelemetryAccumulator.cs`

- [ ] **Step 1: Create the TelemetryAccumulator record**

```csharp
namespace Presentation.AgentHub.DTOs;

/// <summary>
/// Running totals for session-level telemetry. Persisted on the <see cref="ConversationRecord"/>
/// so the stateless AG-UI handler can accumulate metrics across HTTP requests.
/// </summary>
public sealed record TelemetryAccumulator(
    int TurnCount,
    int ToolCallCount,
    int InputTokens,
    int OutputTokens,
    int CacheRead,
    int CacheWrite,
    decimal CostUsd)
{
    /// <summary>Empty accumulator — starting point for a new session.</summary>
    public static readonly TelemetryAccumulator Zero = new(0, 0, 0, 0, 0, 0, 0m);

    /// <summary>Returns a new accumulator with this turn's usage added.</summary>
    public TelemetryAccumulator Add(int inputTokens, int outputTokens, int cacheRead, int cacheWrite, decimal costUsd, int toolCalls) =>
        new(TurnCount + 1, ToolCallCount + toolCalls,
            InputTokens + inputTokens, OutputTokens + outputTokens,
            CacheRead + cacheRead, CacheWrite + cacheWrite,
            CostUsd + costUsd);

    /// <summary>Ratio of cache-read tokens to total input tokens (0..1).</summary>
    public decimal CacheHitRate
    {
        get
        {
            var total = InputTokens + CacheRead;
            return total > 0 ? (decimal)CacheRead / total : 0m;
        }
    }
}
```

- [ ] **Step 2: Verify the file compiles**

Run: `dotnet build src/Content/Presentation/Presentation.AgentHub/Presentation.AgentHub.csproj --no-restore -v q`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/Content/Presentation/Presentation.AgentHub/DTOs/TelemetryAccumulator.cs
git commit -m "feat: add TelemetryAccumulator record for AG-UI session metrics"
```

---

### Task 2: Extend ConversationRecord with telemetry fields

**Files:**
- Modify: `src/Content/Presentation/Presentation.AgentHub/DTOs/ConversationRecord.cs:14-22`

- [ ] **Step 1: Add optional parameters to ConversationRecord**

Replace the record declaration (lines 14-22) with:

```csharp
public sealed record ConversationRecord(
    string Id,
    string AgentName,
    string UserId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ConversationMessage> Messages,
    string? Title = null,
    ConversationSettings? Settings = null,
    Guid? ObservabilitySessionId = null,
    TelemetryAccumulator? Telemetry = null);
```

Both new parameters have defaults (`null`), so all existing callers continue to compile without changes. The JSON serializer will deserialize legacy records with `null` for both fields.

- [ ] **Step 2: Verify the full solution compiles**

Run: `dotnet build src/AgenticHarness.slnx --no-restore -v q`
Expected: Build succeeded. No existing callers break because both fields are optional with defaults.

- [ ] **Step 3: Commit**

```bash
git add src/Content/Presentation/Presentation.AgentHub/DTOs/ConversationRecord.cs
git commit -m "feat: add ObservabilitySessionId and Telemetry to ConversationRecord"
```

---

### Task 3: Add UpdateTelemetryAsync to IConversationStore and implement it

**Files:**
- Modify: `src/Content/Presentation/Presentation.AgentHub/Interfaces/IConversationStore.cs:65`
- Modify: `src/Content/Presentation/Presentation.AgentHub/Services/FileSystemConversationStore.cs:265`

- [ ] **Step 1: Write the failing test**

Create the test first in `src/Content/Tests/Presentation.AgentHub.Tests/ConversationStore/FileSystemConversationStoreAdditionalTests.cs` (or add to the existing file). If the file already has tests, add this test to the existing class:

```csharp
[Fact]
public async Task UpdateTelemetryAsync_PersistsSessionIdAndAccumulator()
{
    // Arrange — create a conversation
    var record = await _store.CreateAsync("test-agent", "user-1");
    var sessionId = Guid.NewGuid();
    var telemetry = new TelemetryAccumulator(1, 2, 100, 50, 30, 10, 0.05m);

    // Act
    await _store.UpdateTelemetryAsync(record.Id, sessionId, telemetry);

    // Assert — re-read and verify persisted values
    var updated = await _store.GetAsync(record.Id);
    updated.Should().NotBeNull();
    updated!.ObservabilitySessionId.Should().Be(sessionId);
    updated.Telemetry.Should().NotBeNull();
    updated.Telemetry!.TurnCount.Should().Be(1);
    updated.Telemetry.ToolCallCount.Should().Be(2);
    updated.Telemetry.InputTokens.Should().Be(100);
    updated.Telemetry.OutputTokens.Should().Be(50);
    updated.Telemetry.CacheRead.Should().Be(30);
    updated.Telemetry.CacheWrite.Should().Be(10);
    updated.Telemetry.CostUsd.Should().Be(0.05m);
}
```

Run: `dotnet test src/Content/Tests/Presentation.AgentHub.Tests --filter "UpdateTelemetryAsync_PersistsSessionIdAndAccumulator" -v q`
Expected: FAIL — `UpdateTelemetryAsync` method does not exist yet.

- [ ] **Step 2: Add the method to IConversationStore**

Append after the `UpdateSettingsAsync` method (after line 64):

```csharp
    /// <summary>
    /// Persists the observability session ID and telemetry accumulator for the specified
    /// conversation. Called by the AG-UI handler after each turn to carry session state
    /// across stateless HTTP requests.
    /// Returns the updated record, or <c>null</c> if the conversation does not exist.
    /// </summary>
    Task<ConversationRecord?> UpdateTelemetryAsync(
        string conversationId,
        Guid observabilitySessionId,
        TelemetryAccumulator telemetry,
        CancellationToken ct = default);
```

- [ ] **Step 3: Implement in FileSystemConversationStore**

Add after the `UpdateSettingsAsync` method (after line 265), following the exact same pattern:

```csharp
    /// <inheritdoc/>
    public async Task<ConversationRecord?> UpdateTelemetryAsync(
        string conversationId,
        Guid observabilitySessionId,
        TelemetryAccumulator telemetry,
        CancellationToken ct = default)
    {
        var path = ResolveAndValidatePath(conversationId);

        await _lock.WaitAsync(ct);
        try
        {
            if (!File.Exists(path))
                return null;

            var json = await File.ReadAllTextAsync(path, ct);
            var existing = JsonSerializer.Deserialize<ConversationRecord>(json, _jsonOptions);
            if (existing is null) return null;

            var updated = existing with
            {
                ObservabilitySessionId = observabilitySessionId,
                Telemetry = telemetry,
                UpdatedAt = DateTimeOffset.UtcNow,
            };

            await WriteAtomicLockedAsync(path, updated, ct);
            return updated;
        }
        finally
        {
            _lock.Release();
        }
    }
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test src/Content/Tests/Presentation.AgentHub.Tests --filter "UpdateTelemetryAsync_PersistsSessionIdAndAccumulator" -v q`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/Content/Presentation/Presentation.AgentHub/Interfaces/IConversationStore.cs \
        src/Content/Presentation/Presentation.AgentHub/Services/FileSystemConversationStore.cs \
        src/Content/Tests/Presentation.AgentHub.Tests/ConversationStore/FileSystemConversationStoreAdditionalTests.cs
git commit -m "feat: add UpdateTelemetryAsync to IConversationStore"
```

---

### Task 4: Fix AgUiRunHandler session lifecycle

**Files:**
- Modify: `src/Content/Presentation/Presentation.AgentHub/AgUi/AgUiRunHandler.cs:120-132` (HandleRunAsync session start)
- Modify: `src/Content/Presentation/Presentation.AgentHub/AgUi/AgUiRunHandler.cs:219-227` (ExecuteRunAsync post-turn)

This is the core fix. Two changes:

- [ ] **Step 1: Fix session start in HandleRunAsync**

Replace lines 120-132 (the `isNewSession` block):

```csharp
        var isNewSession = record.Messages.Count == 0;
        Guid observabilitySessionId = Guid.Empty;
        if (isNewSession)
        {
            var agentTag = new KeyValuePair<string, object?>(AgentConventions.Name, record.AgentName);
            SessionMetrics.SessionsStarted.Add(1, agentTag);
            SessionMetrics.ActiveSessions.Add(1, new TagList { { AgentConventions.Name, record.AgentName } });
            UserActivityMetrics.SessionsStarted.Add(1,
                new KeyValuePair<string, object?>(UserConventions.UserId, callerId));

            observabilitySessionId = await _observabilityStore.StartSessionAsync(
                input.ThreadId, record.AgentName, model: null, ct);
        }
```

With:

```csharp
        var observabilitySessionId = record.ObservabilitySessionId ?? Guid.Empty;
        if (observabilitySessionId == Guid.Empty)
        {
            var agentTag = new KeyValuePair<string, object?>(AgentConventions.Name, record.AgentName);
            SessionMetrics.SessionsStarted.Add(1, agentTag);
            SessionMetrics.ActiveSessions.Add(1, new TagList { { AgentConventions.Name, record.AgentName } });
            UserActivityMetrics.SessionsStarted.Add(1,
                new KeyValuePair<string, object?>(UserConventions.UserId, callerId));

            observabilitySessionId = await _observabilityStore.StartSessionAsync(
                input.ThreadId, record.AgentName, model: null, ct);

            await _conversationStore.UpdateTelemetryAsync(
                input.ThreadId, observabilitySessionId, TelemetryAccumulator.Zero, ct);
        }
```

Key differences:
- Uses `record.ObservabilitySessionId` instead of `record.Messages.Count == 0` — the session ID persists across requests
- After starting the session, immediately persists the ID via `UpdateTelemetryAsync`

- [ ] **Step 2: Add post-turn metric accumulation in ExecuteRunAsync**

After the `UserActivityMetrics.Turns.Add(...)` call (line ~226) and before the streaming section (`// Stream the response as TEXT_MESSAGE_* events`), insert:

```csharp
        // Accumulate session-level metrics and persist to both observability and conversation stores
        var previousTelemetry = record.Telemetry ?? TelemetryAccumulator.Zero;
        var updatedTelemetry = previousTelemetry.Add(
            result.InputTokens, result.OutputTokens,
            result.CacheRead, result.CacheWrite,
            result.CostUsd, result.ToolsInvoked.Count);

        try
        {
            await _observabilityStore.UpdateSessionMetricsAsync(
                observabilitySessionId,
                updatedTelemetry.TurnCount, updatedTelemetry.ToolCallCount, subagentCount: 0,
                updatedTelemetry.InputTokens, updatedTelemetry.OutputTokens,
                updatedTelemetry.CacheRead, updatedTelemetry.CacheWrite,
                updatedTelemetry.CostUsd,
                Math.Round(updatedTelemetry.CacheHitRate, 4),
                result.Model, ct);

            await _conversationStore.UpdateTelemetryAsync(
                input.ThreadId, observabilitySessionId, updatedTelemetry, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "AG-UI run {RunId}: failed to persist session metrics.", input.RunId);
        }
```

- [ ] **Step 3: Verify the solution compiles**

Run: `dotnet build src/AgenticHarness.slnx --no-restore -v q`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add src/Content/Presentation/Presentation.AgentHub/AgUi/AgUiRunHandler.cs
git commit -m "fix: AG-UI handler now persists session telemetry across requests"
```

---

### Task 5: Fix existing tests and add telemetry-specific tests

**Files:**
- Modify: `src/Content/Tests/Presentation.AgentHub.Tests/AgUi/AgUiRunHandlerTests.cs`

The existing `BuildHandler` method is missing the `IObservabilityStore` parameter (4 args vs the constructor's 5). This must be fixed first, then new tests added.

- [ ] **Step 1: Fix BuildHandler to include IObservabilityStore**

Replace the `BuildHandler` method (lines 87-96):

```csharp
    private static AgUiRunHandler BuildHandler(
        Mock<IMediator> mediator,
        Mock<IConversationStore> store)
    {
        return new AgUiRunHandler(
            mediator.Object,
            store.Object,
            new ConversationLockRegistry(),
            NullLogger<AgUiRunHandler>.Instance);
    }
```

With:

```csharp
    private static AgUiRunHandler BuildHandler(
        Mock<IMediator> mediator,
        Mock<IConversationStore> store,
        Mock<IObservabilityStore>? observability = null)
    {
        observability ??= new Mock<IObservabilityStore>();
        observability.Setup(o => o.StartSessionAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Guid.NewGuid());

        return new AgUiRunHandler(
            mediator.Object,
            store.Object,
            observability.Object,
            new ConversationLockRegistry(),
            NullLogger<AgUiRunHandler>.Instance);
    }
```

Add the missing using at the top of the file:

```csharp
using Application.AI.Common.Interfaces;
```

- [ ] **Step 2: Add MakeSuccessResult overload that includes token usage**

Add alongside the existing `MakeSuccessResult` helper (after line 49):

```csharp
    private static AgentTurnResult MakeSuccessResultWithUsage(
        string response, int inputTokens = 150, int outputTokens = 80,
        int cacheRead = 40, int cacheWrite = 10, decimal costUsd = 0.003m,
        string model = "gpt-4o", List<string>? tools = null) =>
        new()
        {
            Success = true,
            Response = response,
            UpdatedHistory = [new ChatMessage(ChatRole.Assistant, response)],
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            CacheRead = cacheRead,
            CacheWrite = cacheWrite,
            CostUsd = costUsd,
            Model = model,
            ToolsInvoked = tools ?? [],
        };
```

- [ ] **Step 3: Add test — first turn persists session metrics**

```csharp
    [Fact]
    public async Task HandleRunAsync_FirstTurn_CreatesSessionAndPersistsMetrics()
    {
        const string threadId = "conv-telemetry-1";
        const string userId = "user-tel-1";
        const string agentResponse = "Here are your tools.";

        var mediator = new Mock<IMediator>();
        var store = new Mock<IConversationStore>();
        var observability = new Mock<IObservabilityStore>();
        var sessionId = Guid.NewGuid();

        var record = MakeRecord(threadId, userId);
        store.Setup(s => s.GetAsync(threadId, It.IsAny<CancellationToken>()))
             .ReturnsAsync(record);
        store.Setup(s => s.GetHistoryForDispatch(threadId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync([]);
        store.Setup(s => s.AppendMessageAsync(threadId, It.IsAny<ConversationMessage>(), It.IsAny<CancellationToken>()))
             .Returns(Task.CompletedTask);
        store.Setup(s => s.UpdateTelemetryAsync(threadId, It.IsAny<Guid>(), It.IsAny<TelemetryAccumulator>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(record);

        observability.Setup(o => o.StartSessionAsync(threadId, "test-agent", null, It.IsAny<CancellationToken>()))
                     .ReturnsAsync(sessionId);

        mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(MakeSuccessResultWithUsage(agentResponse, inputTokens: 100, outputTokens: 50, costUsd: 0.01m));

        var handler = BuildHandler(mediator, store, observability);
        var input = MakeInput(threadId, "What tools do you have?");
        var user = MakeUser(userId);

        using var ms = new MemoryStream();
        var writer = new AgUiEventWriter(ms);

        await handler.HandleRunAsync(input, writer, user);

        // Session was started in the observability store
        observability.Verify(o => o.StartSessionAsync(threadId, "test-agent", null, It.IsAny<CancellationToken>()), Times.Once);

        // Session metrics were updated with non-zero values
        observability.Verify(o => o.UpdateSessionMetricsAsync(
            sessionId,
            1,    // turnCount
            0,    // toolCallCount
            0,    // subagentCount
            100,  // inputTokens
            50,   // outputTokens
            It.IsAny<int>(), It.IsAny<int>(),
            0.01m, // costUsd
            It.IsAny<decimal>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);

        // Telemetry was persisted to conversation store (twice: once for Zero on session start, once after turn)
        store.Verify(s => s.UpdateTelemetryAsync(
            threadId, sessionId,
            It.IsAny<TelemetryAccumulator>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }
```

- [ ] **Step 4: Add test — second turn reuses session and accumulates**

```csharp
    [Fact]
    public async Task HandleRunAsync_SecondTurn_ReusesSessionAndAccumulatesMetrics()
    {
        const string threadId = "conv-telemetry-2";
        const string userId = "user-tel-2";
        var sessionId = Guid.NewGuid();

        var mediator = new Mock<IMediator>();
        var store = new Mock<IConversationStore>();
        var observability = new Mock<IObservabilityStore>();

        // Simulate a conversation that already has one turn completed
        var existingTelemetry = new TelemetryAccumulator(1, 0, 100, 50, 40, 10, 0.01m);
        var record = new ConversationRecord(
            threadId, "test-agent", userId,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            [new ConversationMessage(Guid.NewGuid(), MessageRole.User, "first msg", DateTimeOffset.UtcNow),
             new ConversationMessage(Guid.NewGuid(), MessageRole.Assistant, "first reply", DateTimeOffset.UtcNow)],
            "first msg", null, sessionId, existingTelemetry);

        store.Setup(s => s.GetAsync(threadId, It.IsAny<CancellationToken>()))
             .ReturnsAsync(record);
        store.Setup(s => s.GetHistoryForDispatch(threadId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(record.Messages);
        store.Setup(s => s.AppendMessageAsync(threadId, It.IsAny<ConversationMessage>(), It.IsAny<CancellationToken>()))
             .Returns(Task.CompletedTask);
        store.Setup(s => s.UpdateTelemetryAsync(threadId, sessionId, It.IsAny<TelemetryAccumulator>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(record);

        mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(MakeSuccessResultWithUsage("second reply", inputTokens: 200, outputTokens: 100,
                    cacheRead: 60, cacheWrite: 20, costUsd: 0.02m, tools: ["file_system"]));

        var handler = BuildHandler(mediator, store, observability);
        var input = MakeInput(threadId, "Use a tool please");
        var user = MakeUser(userId);

        using var ms = new MemoryStream();
        var writer = new AgUiEventWriter(ms);

        await handler.HandleRunAsync(input, writer, user);

        // Should NOT start a new session — reuse existing one
        observability.Verify(o => o.StartSessionAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);

        // Session metrics should be accumulated (turn1 + turn2)
        observability.Verify(o => o.UpdateSessionMetricsAsync(
            sessionId,
            2,    // turnCount (1 + 1)
            1,    // toolCallCount (0 + 1)
            0,    // subagentCount
            300,  // inputTokens (100 + 200)
            150,  // outputTokens (50 + 100)
            100,  // cacheRead (40 + 60)
            30,   // cacheWrite (10 + 20)
            0.03m, // costUsd (0.01 + 0.02)
            It.IsAny<decimal>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);

        // Telemetry persisted once (only after turn, no session start)
        store.Verify(s => s.UpdateTelemetryAsync(
            threadId, sessionId,
            It.Is<TelemetryAccumulator>(t => t.TurnCount == 2 && t.ToolCallCount == 1),
            It.IsAny<CancellationToken>()), Times.Once);
    }
```

- [ ] **Step 5: Run all AgUiRunHandler tests**

Run: `dotnet test src/Content/Tests/Presentation.AgentHub.Tests --filter "AgUiRunHandlerTests" -v n`
Expected: All tests pass (existing + new).

- [ ] **Step 6: Run the full test suite**

Run: `dotnet test src/AgenticHarness.slnx -v q`
Expected: All tests pass.

- [ ] **Step 7: Commit**

```bash
git add src/Content/Tests/Presentation.AgentHub.Tests/AgUi/AgUiRunHandlerTests.cs
git commit -m "test: add AG-UI session telemetry persistence tests"
```

---

### Task 6: Final build verification

- [ ] **Step 1: Full solution build**

Run: `dotnet build src/AgenticHarness.slnx -v q`
Expected: Build succeeded, 0 warnings related to new code.

- [ ] **Step 2: Full test suite**

Run: `dotnet test src/AgenticHarness.slnx -v q`
Expected: All tests pass.

- [ ] **Step 3: Verify the fix addresses the root cause**

Checklist:
- `AgUiRunHandler` now calls `UpdateSessionMetricsAsync` after every turn (was: never called)
- `ObservabilitySessionId` persists across AG-UI requests via `ConversationRecord` (was: `Guid.Empty` after first request)
- `ExecuteAgentTurnCommand.ObservabilitySessionId` is always a valid GUID (was: `Guid.Empty` on subsequent turns)
- Session-level metrics (turns, tokens, cost) are accumulated correctly (was: always 0)
- All `RecordMessageAsync` calls in `ExecuteAgentTurnCommandHandler` now receive a valid session ID (was: `Guid.Empty` on turn 2+, so messages were orphaned)
