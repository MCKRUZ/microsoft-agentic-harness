diff --git a/src/Content/Application/Application.AI.Common/Interfaces/Memory/IAgentHistoryStore.cs b/src/Content/Application/Application.AI.Common/Interfaces/Memory/IAgentHistoryStore.cs
new file mode 100644
index 0000000..2c486dd
--- /dev/null
+++ b/src/Content/Application/Application.AI.Common/Interfaces/Memory/IAgentHistoryStore.cs
@@ -0,0 +1,107 @@
+using System.Text.Json;
+
+namespace Application.AI.Common.Interfaces.Memory;
+
+/// <summary>
+/// Immutable record capturing a single agent decision event for the history log.
+/// </summary>
+/// <remarks>
+/// Events are written to <c>decisions.jsonl</c> in the trace run directory. The
+/// <see cref="Sequence"/> is assigned by the store and is monotonically increasing
+/// per store instance. All properties are init-only.
+/// </remarks>
+public record AgentDecisionEvent
+{
+    /// <summary>Monotonically increasing sequence number assigned by the store.</summary>
+    public long Sequence { get; init; }
+
+    /// <summary>UTC timestamp of the event.</summary>
+    public DateTimeOffset Timestamp { get; init; }
+
+    /// <summary>
+    /// Categorizes the event: <c>"tool_call"</c>, <c>"tool_result"</c>,
+    /// <c>"decision"</c>, or <c>"observation"</c>.
+    /// </summary>
+    public required string EventType { get; init; }
+
+    /// <summary>Correlation identifier linking this event to an execution run.</summary>
+    public required string ExecutionRunId { get; init; }
+
+    /// <summary>The conversation turn during which this event occurred.</summary>
+    public required string TurnId { get; init; }
+
+    /// <summary>Tool name for <c>tool_call</c> and <c>tool_result</c> events; null otherwise.</summary>
+    public string? ToolName { get; init; }
+
+    /// <summary>
+    /// Bucketed outcome: <c>"success"</c>, <c>"partial"</c>, <c>"error"</c>,
+    /// <c>"timeout"</c>, or <c>"blocked"</c>. Null for non-result events.
+    /// </summary>
+    public string? ResultCategory { get; init; }
+
+    /// <summary>Optional structured event payload.</summary>
+    public JsonElement? Payload { get; init; }
+}
+
+/// <summary>
+/// Filter parameters for querying the agent decision log.
+/// </summary>
+public record DecisionLogQuery
+{
+    /// <summary>Required. Only events for this execution run are returned.</summary>
+    public required string ExecutionRunId { get; init; }
+
+    /// <summary>Optional. Filter by conversation turn.</summary>
+    public string? TurnId { get; init; }
+
+    /// <summary>
+    /// Optional. Filter by event type: <c>"tool_call"</c>, <c>"tool_result"</c>,
+    /// <c>"decision"</c>, or <c>"observation"</c>.
+    /// </summary>
+    public string? EventType { get; init; }
+
+    /// <summary>Optional. Filter by tool name.</summary>
+    public string? ToolName { get; init; }
+
+    /// <summary>
+    /// Sequence checkpoint. Only events with <c>Sequence &gt; Since</c> are returned.
+    /// Default is 0 (return all events).
+    /// </summary>
+    public long Since { get; init; } = 0;
+
+    /// <summary>Maximum number of events to return. Default is 100.</summary>
+    public int Limit { get; init; } = 100;
+}
+
+/// <summary>
+/// Append-only, queryable log of agent decision events for a single execution run.
+/// Written to <c>decisions.jsonl</c> in the trace run directory.
+/// </summary>
+/// <remarks>
+/// <para>Thread-safe for concurrent appends.</para>
+/// <para>One store instance corresponds to one <see cref="Traces.ITraceWriter"/> instance.</para>
+/// </remarks>
+public interface IAgentHistoryStore
+{
+    /// <summary>
+    /// Appends a decision event to the log. Thread-safe. The <see cref="AgentDecisionEvent.Sequence"/>
+    /// is assigned by the store and monotonically increases across concurrent callers.
+    /// </summary>
+    /// <param name="evt">The event to append. <see cref="AgentDecisionEvent.Sequence"/> is ignored
+    /// and overwritten by the store.</param>
+    /// <param name="cancellationToken">Cancellation token.</param>
+    Task AppendAsync(AgentDecisionEvent evt, CancellationToken cancellationToken = default);
+
+    /// <summary>
+    /// Streams matching events from the log. Filters applied in order: <c>ExecutionRunId</c>,
+    /// <c>EventType</c>, <c>ToolName</c>, <c>TurnId</c>, <c>Since</c> (sequence checkpoint).
+    /// Bounded by <c>Limit</c>.
+    /// </summary>
+    /// <remarks>
+    /// Returns an empty sequence if <c>decisions.jsonl</c> does not exist. Never throws for
+    /// missing files — only for I/O errors.
+    /// </remarks>
+    IAsyncEnumerable<AgentDecisionEvent> QueryAsync(
+        DecisionLogQuery query,
+        CancellationToken cancellationToken = default);
+}
diff --git a/src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs b/src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs
index c1f05bd..904c9d7 100644
--- a/src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs
+++ b/src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs
@@ -1,6 +1,8 @@
 using Application.AI.Common.Interfaces;
 using Application.AI.Common.Interfaces.A2A;
+using Application.AI.Common.Interfaces.Memory;
 using Application.AI.Common.Interfaces.Traces;
+using Infrastructure.AI.Memory;
 using Infrastructure.AI.Security;
 using Infrastructure.AI.Traces;
 using Application.AI.Common.Interfaces.Agent;
@@ -169,6 +171,13 @@ public static class DependencyInjection
         // Config discovery — directory walk with @include support
         services.AddTransient<IConfigDiscoveryService, DirectoryWalkConfigDiscovery>();
 
+        // Agent history store factory — creates a JsonlAgentHistoryStore for a given execution run.
+        // IAgentHistoryStore instances are run-scoped (one per ITraceWriter) and created by
+        // AgentExecutionContextFactory (section 14), not by the DI container directly.
+        // Register the factory delegate for use by the context factory.
+        services.AddSingleton<Func<ITraceWriter, IAgentHistoryStore>>(
+            _ => tw => new JsonlAgentHistoryStore(tw));
+
         return services;
     }
 
diff --git a/src/Content/Infrastructure/Infrastructure.AI/Memory/JsonlAgentHistoryStore.cs b/src/Content/Infrastructure/Infrastructure.AI/Memory/JsonlAgentHistoryStore.cs
new file mode 100644
index 0000000..3831106
--- /dev/null
+++ b/src/Content/Infrastructure/Infrastructure.AI/Memory/JsonlAgentHistoryStore.cs
@@ -0,0 +1,137 @@
+using System.Runtime.CompilerServices;
+using System.Text.Json;
+using Application.AI.Common.Interfaces.Memory;
+using Application.AI.Common.Interfaces.Traces;
+
+namespace Infrastructure.AI.Memory;
+
+/// <summary>
+/// Filesystem-backed implementation of <see cref="IAgentHistoryStore"/> that writes
+/// <see cref="AgentDecisionEvent"/> records to <c>decisions.jsonl</c> in the trace run directory.
+/// </summary>
+/// <remarks>
+/// <para>
+/// Uses its own <see cref="SemaphoreSlim"/> for <c>decisions.jsonl</c> — separate from the
+/// <see cref="ITraceWriter"/>'s internal lock for <c>traces.jsonl</c>. The two files never
+/// contend with each other, keeping append throughput higher.
+/// </para>
+/// <para>
+/// <see cref="AgentDecisionEvent.Sequence"/> is assigned via <see cref="Interlocked.Increment"/>
+/// before the semaphore is acquired, so sequence number allocation is lock-free while JSONL
+/// write ordering is serialized.
+/// </para>
+/// <para>
+/// Scoped per execution run — one instance per <see cref="ITraceWriter"/>. Created by
+/// <c>AgentExecutionContextFactory</c> alongside the writer, not by the DI container directly.
+/// </para>
+/// </remarks>
+public sealed class JsonlAgentHistoryStore : IAgentHistoryStore
+{
+    private static readonly JsonSerializerOptions SerializeOptions = new()
+    {
+        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
+        WriteIndented = false
+    };
+
+    private static readonly JsonSerializerOptions DeserializeOptions = new()
+    {
+        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
+    };
+
+    private readonly string _decisionsPath;
+    private readonly SemaphoreSlim _writeLock = new(1, 1);
+    private long _sequenceCounter;
+
+    /// <summary>
+    /// Initializes a new instance of <see cref="JsonlAgentHistoryStore"/>.
+    /// </summary>
+    /// <param name="traceWriter">
+    /// The scoped trace writer for this execution run. Provides the run directory path.
+    /// </param>
+    public JsonlAgentHistoryStore(ITraceWriter traceWriter)
+    {
+        _decisionsPath = Path.Combine(traceWriter.RunDirectory, "decisions.jsonl");
+    }
+
+    /// <inheritdoc />
+    public async Task AppendAsync(AgentDecisionEvent evt, CancellationToken cancellationToken = default)
+    {
+        // Assign sequence number lock-free; write ordering is serialized by the semaphore.
+        var seq = Interlocked.Increment(ref _sequenceCounter);
+
+        var finalEvent = evt with
+        {
+            Sequence = seq,
+            Timestamp = evt.Timestamp == default ? DateTimeOffset.UtcNow : evt.Timestamp
+        };
+
+        var line = JsonSerializer.Serialize(finalEvent, SerializeOptions) + "\n";
+
+        await _writeLock.WaitAsync(cancellationToken);
+        try
+        {
+            await File.AppendAllTextAsync(_decisionsPath, line, cancellationToken);
+        }
+        finally
+        {
+            _writeLock.Release();
+        }
+    }
+
+    /// <inheritdoc />
+    public async IAsyncEnumerable<AgentDecisionEvent> QueryAsync(
+        DecisionLogQuery query,
+        [EnumeratorCancellation] CancellationToken cancellationToken = default)
+    {
+        if (!File.Exists(_decisionsPath))
+            yield break;
+
+        var yielded = 0;
+
+        using var stream = new FileStream(
+            _decisionsPath,
+            FileMode.Open,
+            FileAccess.Read,
+            FileShare.ReadWrite);
+        using var reader = new StreamReader(stream);
+
+        while (!reader.EndOfStream && yielded < query.Limit)
+        {
+            cancellationToken.ThrowIfCancellationRequested();
+
+            var line = await reader.ReadLineAsync(cancellationToken);
+            if (string.IsNullOrWhiteSpace(line))
+                continue;
+
+            AgentDecisionEvent? evt;
+            try
+            {
+                evt = JsonSerializer.Deserialize<AgentDecisionEvent>(line, DeserializeOptions);
+            }
+            catch (JsonException)
+            {
+                // Corrupted line — skip
+                continue;
+            }
+
+            if (evt is null) continue;
+
+            if (!string.Equals(evt.ExecutionRunId, query.ExecutionRunId, StringComparison.Ordinal))
+                continue;
+            if (query.EventType is not null &&
+                !string.Equals(evt.EventType, query.EventType, StringComparison.Ordinal))
+                continue;
+            if (query.ToolName is not null &&
+                !string.Equals(evt.ToolName, query.ToolName, StringComparison.Ordinal))
+                continue;
+            if (query.TurnId is not null &&
+                !string.Equals(evt.TurnId, query.TurnId, StringComparison.Ordinal))
+                continue;
+            if (evt.Sequence <= query.Since)
+                continue;
+
+            yield return evt;
+            yielded++;
+        }
+    }
+}
diff --git a/src/Content/Infrastructure/Infrastructure.AI/Tools/ReadHistoryTool.cs b/src/Content/Infrastructure/Infrastructure.AI/Tools/ReadHistoryTool.cs
new file mode 100644
index 0000000..b4516d4
--- /dev/null
+++ b/src/Content/Infrastructure/Infrastructure.AI/Tools/ReadHistoryTool.cs
@@ -0,0 +1,102 @@
+using System.Text.Json;
+using Application.AI.Common.Interfaces.Memory;
+using Application.AI.Common.Interfaces.Tools;
+using Domain.AI.Models;
+
+namespace Infrastructure.AI.Tools;
+
+/// <summary>
+/// Tool keyed <c>"read_history"</c>. Queries the agent decision log for a specific
+/// execution run and returns matching events as a JSON array.
+/// </summary>
+/// <remarks>
+/// <para>
+/// Safe to call with unknown or nonexistent run IDs — returns <c>"[]"</c> rather than throwing.
+/// </para>
+/// <para>
+/// <strong>Parameters (passed via <c>execution</c> operation):</strong>
+/// <list type="bullet">
+///   <item><c>execution_run_id</c> (string, required) — which run to query</item>
+///   <item><c>event_type</c> (string, optional) — filter by event type</item>
+///   <item><c>tool_name</c> (string, optional) — filter by tool name</item>
+///   <item><c>since</c> (long, optional, default 0) — sequence checkpoint; only events after this sequence are returned</item>
+///   <item><c>limit</c> (int, optional, default 100) — max results</item>
+/// </list>
+/// </para>
+/// </remarks>
+public sealed class ReadHistoryTool : ITool
+{
+    private static readonly JsonSerializerOptions SerializeOptions = new()
+    {
+        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
+        WriteIndented = false
+    };
+
+    private readonly IAgentHistoryStore _historyStore;
+
+    /// <summary>Initializes a new instance of <see cref="ReadHistoryTool"/>.</summary>
+    public ReadHistoryTool(IAgentHistoryStore historyStore)
+    {
+        _historyStore = historyStore;
+    }
+
+    /// <inheritdoc />
+    public string Name => "read_history";
+
+    /// <inheritdoc />
+    public string Description =>
+        "Query the agent decision log for a specific execution run. " +
+        "Returns a JSON array of decision events filtered by run ID, event type, tool name, " +
+        "and sequence checkpoint. Returns '[]' for unknown run IDs.";
+
+    /// <inheritdoc />
+    public IReadOnlyList<string> SupportedOperations { get; } = ["query"];
+
+    /// <inheritdoc />
+    public bool IsReadOnly => true;
+
+    /// <inheritdoc />
+    public bool IsConcurrencySafe => true;
+
+    /// <inheritdoc />
+    public async Task<ToolResult> ExecuteAsync(
+        string operation,
+        IReadOnlyDictionary<string, object?> parameters,
+        CancellationToken cancellationToken = default)
+    {
+        if (!string.Equals(operation, "query", StringComparison.Ordinal))
+            return ToolResult.Fail($"ReadHistoryTool does not support operation '{operation}'. Supported: query");
+
+        var runId = GetString(parameters, "execution_run_id");
+        if (string.IsNullOrEmpty(runId))
+            return ToolResult.Ok("[]");
+
+        var query = new DecisionLogQuery
+        {
+            ExecutionRunId = runId,
+            EventType = GetString(parameters, "event_type"),
+            ToolName = GetString(parameters, "tool_name"),
+            Since = GetLong(parameters, "since"),
+            Limit = GetInt(parameters, "limit", 100)
+        };
+
+        var events = new List<AgentDecisionEvent>();
+        await foreach (var evt in _historyStore.QueryAsync(query, cancellationToken))
+            events.Add(evt);
+
+        return ToolResult.Ok(JsonSerializer.Serialize(events, SerializeOptions));
+    }
+
+    private static string? GetString(IReadOnlyDictionary<string, object?> p, string key) =>
+        p.TryGetValue(key, out var v) ? v?.ToString() : null;
+
+    private static long GetLong(IReadOnlyDictionary<string, object?> p, string key) =>
+        p.TryGetValue(key, out var v) && v is not null
+            ? Convert.ToInt64(v)
+            : 0L;
+
+    private static int GetInt(IReadOnlyDictionary<string, object?> p, string key, int defaultValue) =>
+        p.TryGetValue(key, out var v) && v is not null
+            ? Convert.ToInt32(v)
+            : defaultValue;
+}
diff --git a/src/Content/Tests/Infrastructure.AI.Tests/Memory/JsonlAgentHistoryStoreTests.cs b/src/Content/Tests/Infrastructure.AI.Tests/Memory/JsonlAgentHistoryStoreTests.cs
new file mode 100644
index 0000000..ae7bdca
--- /dev/null
+++ b/src/Content/Tests/Infrastructure.AI.Tests/Memory/JsonlAgentHistoryStoreTests.cs
@@ -0,0 +1,244 @@
+using System.Runtime.CompilerServices;
+using System.Text.Json;
+using Application.AI.Common.Interfaces.Memory;
+using Application.AI.Common.Interfaces.Traces;
+using Domain.Common.MetaHarness;
+using FluentAssertions;
+using Infrastructure.AI.Memory;
+using Moq;
+using Xunit;
+
+namespace Infrastructure.AI.Tests.Memory;
+
+public sealed class JsonlAgentHistoryStoreTests : IDisposable
+{
+    private readonly string _runDir;
+    private readonly Mock<ITraceWriter> _traceWriterMock;
+
+    public JsonlAgentHistoryStoreTests()
+    {
+        _runDir = Path.Combine(Path.GetTempPath(), $"history-store-tests-{Guid.NewGuid():N}");
+        Directory.CreateDirectory(_runDir);
+
+        _traceWriterMock = new Mock<ITraceWriter>();
+        _traceWriterMock.Setup(w => w.RunDirectory).Returns(_runDir);
+    }
+
+    public void Dispose()
+    {
+        if (Directory.Exists(_runDir))
+            Directory.Delete(_runDir, recursive: true);
+    }
+
+    private JsonlAgentHistoryStore CreateStore() => new(_traceWriterMock.Object);
+
+    private static AgentDecisionEvent BuildEvent(
+        string runId = "run-001",
+        string turnId = "turn-1",
+        string eventType = "tool_call",
+        string? toolName = "file_read",
+        string? resultCategory = null) => new()
+    {
+        EventType = eventType,
+        ExecutionRunId = runId,
+        TurnId = turnId,
+        ToolName = toolName,
+        ResultCategory = resultCategory,
+        Timestamp = DateTimeOffset.UtcNow
+    };
+
+    // -------------------------------------------------------------------------
+    // AppendAsync
+    // -------------------------------------------------------------------------
+
+    [Fact]
+    public async Task AppendAsync_WritesDecisionEventRecord_ToDecisionsJsonl()
+    {
+        var store = CreateStore();
+        var evt = BuildEvent();
+
+        await store.AppendAsync(evt);
+
+        var decisionsPath = Path.Combine(_runDir, "decisions.jsonl");
+        File.Exists(decisionsPath).Should().BeTrue();
+
+        var line = await File.ReadAllTextAsync(decisionsPath);
+        var deserialized = JsonSerializer.Deserialize<AgentDecisionEvent>(
+            line.Trim(),
+            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
+
+        deserialized.Should().NotBeNull();
+        deserialized!.EventType.Should().Be(evt.EventType);
+        deserialized.ExecutionRunId.Should().Be(evt.ExecutionRunId);
+        deserialized.TurnId.Should().Be(evt.TurnId);
+        deserialized.ToolName.Should().Be(evt.ToolName);
+    }
+
+    [Fact]
+    public async Task AppendAsync_SequenceNumbers_AreMonotonicallyIncreasing()
+    {
+        var store = CreateStore();
+
+        await store.AppendAsync(BuildEvent());
+        await store.AppendAsync(BuildEvent());
+        await store.AppendAsync(BuildEvent());
+
+        var lines = await File.ReadAllLinesAsync(Path.Combine(_runDir, "decisions.jsonl"));
+        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
+        var sequences = lines
+            .Where(l => !string.IsNullOrWhiteSpace(l))
+            .Select(l => JsonSerializer.Deserialize<AgentDecisionEvent>(l, options)!.Sequence)
+            .ToList();
+
+        sequences.Should().HaveCount(3);
+        sequences.Should().BeInAscendingOrder();
+        sequences.Should().OnlyHaveUniqueItems();
+    }
+
+    [Fact]
+    public async Task AppendAsync_ConcurrentAppends_DoNotCorruptFile()
+    {
+        var store = CreateStore();
+        const int taskCount = 10;
+        const int appendsPerTask = 5;
+
+        var tasks = Enumerable.Range(0, taskCount)
+            .Select(_ => Task.Run(async () =>
+            {
+                for (var i = 0; i < appendsPerTask; i++)
+                    await store.AppendAsync(BuildEvent());
+            }));
+
+        await Task.WhenAll(tasks);
+
+        var lines = (await File.ReadAllLinesAsync(Path.Combine(_runDir, "decisions.jsonl")))
+            .Where(l => !string.IsNullOrWhiteSpace(l))
+            .ToList();
+
+        lines.Should().HaveCount(taskCount * appendsPerTask);
+
+        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
+        foreach (var line in lines)
+        {
+            var act = () => JsonSerializer.Deserialize<AgentDecisionEvent>(line, options);
+            act.Should().NotThrow("every line must be valid JSON");
+        }
+    }
+
+    // -------------------------------------------------------------------------
+    // QueryAsync
+    // -------------------------------------------------------------------------
+
+    [Fact]
+    public async Task QueryAsync_NoFilters_ReturnsAllRecords()
+    {
+        var store = CreateStore();
+        const string runId = "run-all";
+        for (var i = 0; i < 5; i++)
+            await store.AppendAsync(BuildEvent(runId: runId));
+
+        var results = await ToListAsync(store.QueryAsync(new DecisionLogQuery { ExecutionRunId = runId }));
+
+        results.Should().HaveCount(5);
+    }
+
+    [Fact]
+    public async Task QueryAsync_FilterByEventType_ReturnsMatchingOnly()
+    {
+        var store = CreateStore();
+        const string runId = "run-filter-type";
+        for (var i = 0; i < 3; i++)
+            await store.AppendAsync(BuildEvent(runId: runId, eventType: "tool_call"));
+        for (var i = 0; i < 2; i++)
+            await store.AppendAsync(BuildEvent(runId: runId, eventType: "decision"));
+
+        var results = await ToListAsync(store.QueryAsync(new DecisionLogQuery
+        {
+            ExecutionRunId = runId,
+            EventType = "tool_call"
+        }));
+
+        results.Should().HaveCount(3);
+        results.Should().AllSatisfy(e => e.EventType.Should().Be("tool_call"));
+    }
+
+    [Fact]
+    public async Task QueryAsync_FilterByToolName_ReturnsMatchingOnly()
+    {
+        var store = CreateStore();
+        const string runId = "run-filter-tool";
+        await store.AppendAsync(BuildEvent(runId: runId, toolName: "read_history"));
+        await store.AppendAsync(BuildEvent(runId: runId, toolName: "file_read"));
+        await store.AppendAsync(BuildEvent(runId: runId, toolName: "read_history"));
+
+        var results = await ToListAsync(store.QueryAsync(new DecisionLogQuery
+        {
+            ExecutionRunId = runId,
+            ToolName = "read_history"
+        }));
+
+        results.Should().HaveCount(2);
+        results.Should().AllSatisfy(e => e.ToolName.Should().Be("read_history"));
+    }
+
+    [Fact]
+    public async Task QueryAsync_WithSince_SkipsRecordsAtOrBeforeSequence()
+    {
+        var store = CreateStore();
+        const string runId = "run-since";
+        for (var i = 0; i < 5; i++)
+            await store.AppendAsync(BuildEvent(runId: runId));
+
+        var results = await ToListAsync(store.QueryAsync(new DecisionLogQuery
+        {
+            ExecutionRunId = runId,
+            Since = 3
+        }));
+
+        results.Should().HaveCount(2);
+        results.Should().AllSatisfy(e => e.Sequence.Should().BeGreaterThan(3));
+    }
+
+    [Fact]
+    public async Task QueryAsync_WithLimit_ReturnsBoundedResults()
+    {
+        var store = CreateStore();
+        const string runId = "run-limit";
+        for (var i = 0; i < 10; i++)
+            await store.AppendAsync(BuildEvent(runId: runId));
+
+        var results = await ToListAsync(store.QueryAsync(new DecisionLogQuery
+        {
+            ExecutionRunId = runId,
+            Limit = 3
+        }));
+
+        results.Should().HaveCount(3);
+    }
+
+    [Fact]
+    public async Task QueryAsync_WhenFileDoesNotExist_ReturnsEmpty()
+    {
+        var store = CreateStore();
+        // No AppendAsync called — no file on disk
+
+        var results = await ToListAsync(store.QueryAsync(new DecisionLogQuery
+        {
+            ExecutionRunId = "nonexistent-run"
+        }));
+
+        results.Should().BeEmpty();
+    }
+
+    // -------------------------------------------------------------------------
+    // Helpers
+    // -------------------------------------------------------------------------
+
+    private static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> source)
+    {
+        var list = new List<T>();
+        await foreach (var item in source)
+            list.Add(item);
+        return list;
+    }
+}
diff --git a/src/Content/Tests/Infrastructure.AI.Tests/Memory/ReadHistoryToolTests.cs b/src/Content/Tests/Infrastructure.AI.Tests/Memory/ReadHistoryToolTests.cs
new file mode 100644
index 0000000..54a863a
--- /dev/null
+++ b/src/Content/Tests/Infrastructure.AI.Tests/Memory/ReadHistoryToolTests.cs
@@ -0,0 +1,140 @@
+using System.Text.Json;
+using Application.AI.Common.Interfaces.Memory;
+using FluentAssertions;
+using Infrastructure.AI.Memory;
+using Infrastructure.AI.Tools;
+using Moq;
+using Xunit;
+
+namespace Infrastructure.AI.Tests.Memory;
+
+public sealed class ReadHistoryToolTests
+{
+    private static Mock<IAgentHistoryStore> BuildStoreMock(IReadOnlyList<AgentDecisionEvent> events)
+    {
+        var mock = new Mock<IAgentHistoryStore>();
+        mock.Setup(s => s.QueryAsync(It.IsAny<DecisionLogQuery>(), It.IsAny<CancellationToken>()))
+            .Returns<DecisionLogQuery, CancellationToken>((query, _) =>
+            {
+                var filtered = events
+                    .Where(e => e.ExecutionRunId == query.ExecutionRunId)
+                    .Where(e => query.EventType is null || e.EventType == query.EventType)
+                    .Where(e => query.ToolName is null || e.ToolName == query.ToolName)
+                    .Where(e => e.Sequence > query.Since)
+                    .Take(query.Limit)
+                    .ToAsyncEnumerable();
+                return filtered;
+            });
+        return mock;
+    }
+
+    private static AgentDecisionEvent MakeEvent(long seq, string runId, string eventType = "tool_call") => new()
+    {
+        Sequence = seq,
+        EventType = eventType,
+        ExecutionRunId = runId,
+        TurnId = "t1",
+        Timestamp = DateTimeOffset.UtcNow
+    };
+
+    [Fact]
+    public async Task Execute_WithValidRunId_ReturnsSerializedEvents()
+    {
+        var events = new[]
+        {
+            MakeEvent(1, "run-A"),
+            MakeEvent(2, "run-A"),
+            MakeEvent(3, "run-A")
+        };
+        var store = BuildStoreMock(events);
+        var tool = new ReadHistoryTool(store.Object);
+
+        var parameters = new Dictionary<string, object?> { ["execution_run_id"] = "run-A" };
+        var result = await tool.ExecuteAsync("query", parameters);
+
+        result.Success.Should().BeTrue();
+        using var doc = JsonDocument.Parse(result.Output!);
+        doc.RootElement.GetArrayLength().Should().Be(3);
+    }
+
+    [Fact]
+    public async Task Execute_WithSinceParameter_OnlyReturnsNewerEvents()
+    {
+        var events = Enumerable.Range(1, 5)
+            .Select(i => MakeEvent(i, "run-B"))
+            .ToArray();
+        var store = BuildStoreMock(events);
+        var tool = new ReadHistoryTool(store.Object);
+
+        var parameters = new Dictionary<string, object?>
+        {
+            ["execution_run_id"] = "run-B",
+            ["since"] = 3L
+        };
+        var result = await tool.ExecuteAsync("query", parameters);
+
+        result.Success.Should().BeTrue();
+        using var doc = JsonDocument.Parse(result.Output!);
+        doc.RootElement.GetArrayLength().Should().Be(2);
+    }
+
+    [Fact]
+    public async Task Execute_ExceedsLimit_TruncatesToLimit()
+    {
+        var events = Enumerable.Range(1, 20)
+            .Select(i => MakeEvent(i, "run-C"))
+            .ToArray();
+        var store = BuildStoreMock(events);
+        var tool = new ReadHistoryTool(store.Object);
+
+        var parameters = new Dictionary<string, object?>
+        {
+            ["execution_run_id"] = "run-C",
+            ["limit"] = 5
+        };
+        var result = await tool.ExecuteAsync("query", parameters);
+
+        result.Success.Should().BeTrue();
+        using var doc = JsonDocument.Parse(result.Output!);
+        doc.RootElement.GetArrayLength().Should().Be(5);
+    }
+
+    [Fact]
+    public async Task Execute_InvalidRunId_ReturnsEmptyArray()
+    {
+        var store = BuildStoreMock(Array.Empty<AgentDecisionEvent>());
+        var tool = new ReadHistoryTool(store.Object);
+
+        var parameters = new Dictionary<string, object?> { ["execution_run_id"] = "nonexistent" };
+        var result = await tool.ExecuteAsync("query", parameters);
+
+        result.Success.Should().BeTrue();
+        using var doc = JsonDocument.Parse(result.Output!);
+        doc.RootElement.GetArrayLength().Should().Be(0);
+    }
+
+    [Fact]
+    public async Task Execute_MissingRunId_ReturnsEmptyArray()
+    {
+        var store = BuildStoreMock(Array.Empty<AgentDecisionEvent>());
+        var tool = new ReadHistoryTool(store.Object);
+
+        var parameters = new Dictionary<string, object?>();
+        var result = await tool.ExecuteAsync("query", parameters);
+
+        result.Success.Should().BeTrue();
+        result.Output.Should().Be("[]");
+    }
+
+    [Fact]
+    public async Task Execute_UnsupportedOperation_ReturnsFail()
+    {
+        var store = new Mock<IAgentHistoryStore>();
+        var tool = new ReadHistoryTool(store.Object);
+
+        var result = await tool.ExecuteAsync("delete", new Dictionary<string, object?>());
+
+        result.Success.Should().BeFalse();
+        result.Error.Should().Contain("delete");
+    }
+}
