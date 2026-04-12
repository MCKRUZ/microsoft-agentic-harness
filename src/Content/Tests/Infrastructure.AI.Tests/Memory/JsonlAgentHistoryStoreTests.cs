using System.Runtime.CompilerServices;
using System.Text.Json;
using Application.AI.Common.Interfaces.Memory;
using Application.AI.Common.Interfaces.Traces;
using Domain.Common.MetaHarness;
using FluentAssertions;
using Infrastructure.AI.Memory;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Memory;

public sealed class JsonlAgentHistoryStoreTests : IDisposable
{
    private readonly string _runDir;
    private readonly Mock<ITraceWriter> _traceWriterMock;

    public JsonlAgentHistoryStoreTests()
    {
        _runDir = Path.Combine(Path.GetTempPath(), $"history-store-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_runDir);

        _traceWriterMock = new Mock<ITraceWriter>();
        _traceWriterMock.Setup(w => w.RunDirectory).Returns(_runDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_runDir))
            Directory.Delete(_runDir, recursive: true);
    }

    private JsonlAgentHistoryStore CreateStore() => new(_traceWriterMock.Object);

    private static AgentDecisionEvent BuildEvent(
        string runId = "run-001",
        string turnId = "turn-1",
        string eventType = "tool_call",
        string? toolName = "file_read",
        string? resultCategory = null) => new()
    {
        EventType = eventType,
        ExecutionRunId = runId,
        TurnId = turnId,
        ToolName = toolName,
        ResultCategory = resultCategory,
        Timestamp = DateTimeOffset.UtcNow
    };

    // -------------------------------------------------------------------------
    // AppendAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task AppendAsync_WritesDecisionEventRecord_ToDecisionsJsonl()
    {
        var store = CreateStore();
        var evt = BuildEvent();

        await store.AppendAsync(evt);

        var decisionsPath = Path.Combine(_runDir, "decisions.jsonl");
        File.Exists(decisionsPath).Should().BeTrue();

        var line = await File.ReadAllTextAsync(decisionsPath);
        var deserialized = JsonSerializer.Deserialize<AgentDecisionEvent>(
            line.Trim(),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });

        deserialized.Should().NotBeNull();
        deserialized!.EventType.Should().Be(evt.EventType);
        deserialized.ExecutionRunId.Should().Be(evt.ExecutionRunId);
        deserialized.TurnId.Should().Be(evt.TurnId);
        deserialized.ToolName.Should().Be(evt.ToolName);
    }

    [Fact]
    public async Task AppendAsync_SequenceNumbers_AreMonotonicallyIncreasing()
    {
        var store = CreateStore();

        await store.AppendAsync(BuildEvent());
        await store.AppendAsync(BuildEvent());
        await store.AppendAsync(BuildEvent());

        var lines = await File.ReadAllLinesAsync(Path.Combine(_runDir, "decisions.jsonl"));
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        var sequences = lines
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => JsonSerializer.Deserialize<AgentDecisionEvent>(l, options)!.Sequence)
            .ToList();

        sequences.Should().HaveCount(3);
        sequences.Should().BeInAscendingOrder();
        sequences.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task AppendAsync_ConcurrentAppends_DoNotCorruptFile()
    {
        var store = CreateStore();
        const int taskCount = 10;
        const int appendsPerTask = 5;

        var tasks = Enumerable.Range(0, taskCount)
            .Select(_ => Task.Run(async () =>
            {
                for (var i = 0; i < appendsPerTask; i++)
                    await store.AppendAsync(BuildEvent());
            }));

        await Task.WhenAll(tasks);

        var lines = (await File.ReadAllLinesAsync(Path.Combine(_runDir, "decisions.jsonl")))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        lines.Should().HaveCount(taskCount * appendsPerTask);

        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        foreach (var line in lines)
        {
            var act = () => JsonSerializer.Deserialize<AgentDecisionEvent>(line, options);
            act.Should().NotThrow("every line must be valid JSON");
        }
    }

    // -------------------------------------------------------------------------
    // QueryAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task QueryAsync_NoFilters_ReturnsAllRecords()
    {
        var store = CreateStore();
        const string runId = "run-all";
        for (var i = 0; i < 5; i++)
            await store.AppendAsync(BuildEvent(runId: runId));

        var results = await ToListAsync(store.QueryAsync(new DecisionLogQuery { ExecutionRunId = runId }));

        results.Should().HaveCount(5);
    }

    [Fact]
    public async Task QueryAsync_FilterByEventType_ReturnsMatchingOnly()
    {
        var store = CreateStore();
        const string runId = "run-filter-type";
        for (var i = 0; i < 3; i++)
            await store.AppendAsync(BuildEvent(runId: runId, eventType: "tool_call"));
        for (var i = 0; i < 2; i++)
            await store.AppendAsync(BuildEvent(runId: runId, eventType: "decision"));

        var results = await ToListAsync(store.QueryAsync(new DecisionLogQuery
        {
            ExecutionRunId = runId,
            EventType = "tool_call"
        }));

        results.Should().HaveCount(3);
        results.Should().AllSatisfy(e => e.EventType.Should().Be("tool_call"));
    }

    [Fact]
    public async Task QueryAsync_FilterByToolName_ReturnsMatchingOnly()
    {
        var store = CreateStore();
        const string runId = "run-filter-tool";
        await store.AppendAsync(BuildEvent(runId: runId, toolName: "read_history"));
        await store.AppendAsync(BuildEvent(runId: runId, toolName: "file_read"));
        await store.AppendAsync(BuildEvent(runId: runId, toolName: "read_history"));

        var results = await ToListAsync(store.QueryAsync(new DecisionLogQuery
        {
            ExecutionRunId = runId,
            ToolName = "read_history"
        }));

        results.Should().HaveCount(2);
        results.Should().AllSatisfy(e => e.ToolName.Should().Be("read_history"));
    }

    [Fact]
    public async Task QueryAsync_WithSince_SkipsRecordsAtOrBeforeSequence()
    {
        var store = CreateStore();
        const string runId = "run-since";
        for (var i = 0; i < 5; i++)
            await store.AppendAsync(BuildEvent(runId: runId));

        var results = await ToListAsync(store.QueryAsync(new DecisionLogQuery
        {
            ExecutionRunId = runId,
            Since = 3
        }));

        results.Should().HaveCount(2);
        results.Should().AllSatisfy(e => e.Sequence.Should().BeGreaterThan(3));
    }

    [Fact]
    public async Task QueryAsync_WithLimit_ReturnsBoundedResults()
    {
        var store = CreateStore();
        const string runId = "run-limit";
        for (var i = 0; i < 10; i++)
            await store.AppendAsync(BuildEvent(runId: runId));

        var results = await ToListAsync(store.QueryAsync(new DecisionLogQuery
        {
            ExecutionRunId = runId,
            Limit = 3
        }));

        results.Should().HaveCount(3);
    }

    [Fact]
    public async Task QueryAsync_WhenFileDoesNotExist_ReturnsEmpty()
    {
        var store = CreateStore();
        // No AppendAsync called — no file on disk

        var results = await ToListAsync(store.QueryAsync(new DecisionLogQuery
        {
            ExecutionRunId = "nonexistent-run"
        }));

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task QueryAsync_FilterByTurnId_ReturnsMatchingOnly()
    {
        var store = CreateStore();
        const string runId = "run-filter-turn";
        await store.AppendAsync(BuildEvent(runId: runId, turnId: "turn-1"));
        await store.AppendAsync(BuildEvent(runId: runId, turnId: "turn-2"));
        await store.AppendAsync(BuildEvent(runId: runId, turnId: "turn-1"));

        var results = await ToListAsync(store.QueryAsync(new DecisionLogQuery
        {
            ExecutionRunId = runId,
            TurnId = "turn-1"
        }));

        results.Should().HaveCount(2);
        results.Should().AllSatisfy(e => e.TurnId.Should().Be("turn-1"));
    }

    [Fact]
    public async Task QueryAsync_WithCorruptedLine_SkipsCorruptedAndReturnsValid()
    {
        var store = CreateStore();
        const string runId = "run-corrupt";

        // Append a valid event, then write a corrupted line directly, then append another valid event
        await store.AppendAsync(BuildEvent(runId: runId));
        await File.AppendAllTextAsync(Path.Combine(_runDir, "decisions.jsonl"), "NOT_VALID_JSON\n");
        await store.AppendAsync(BuildEvent(runId: runId));

        var results = await ToListAsync(store.QueryAsync(new DecisionLogQuery
        {
            ExecutionRunId = runId
        }));

        // Corrupted line is skipped; 2 valid events returned
        results.Should().HaveCount(2);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
            list.Add(item);
        return list;
    }
}
