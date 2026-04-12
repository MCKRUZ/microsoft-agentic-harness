using System.Text.Json;
using Application.AI.Common.Interfaces.Memory;
using FluentAssertions;
using Infrastructure.AI.Memory;
using Infrastructure.AI.Tools;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Memory;

public sealed class ReadHistoryToolTests
{
    private static Mock<IAgentHistoryStore> BuildStoreMock(IReadOnlyList<AgentDecisionEvent> events)
    {
        var mock = new Mock<IAgentHistoryStore>();
        mock.Setup(s => s.QueryAsync(It.IsAny<DecisionLogQuery>(), It.IsAny<CancellationToken>()))
            .Returns<DecisionLogQuery, CancellationToken>((query, _) =>
            {
                var filtered = events
                    .Where(e => e.ExecutionRunId == query.ExecutionRunId)
                    .Where(e => query.EventType is null || e.EventType == query.EventType)
                    .Where(e => query.ToolName is null || e.ToolName == query.ToolName)
                    .Where(e => e.Sequence > query.Since)
                    .Take(query.Limit)
                    .ToAsyncEnumerable();
                return filtered;
            });
        return mock;
    }

    private static AgentDecisionEvent MakeEvent(long seq, string runId, string eventType = "tool_call") => new()
    {
        Sequence = seq,
        EventType = eventType,
        ExecutionRunId = runId,
        TurnId = "t1",
        Timestamp = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task Execute_WithValidRunId_ReturnsSerializedEvents()
    {
        var events = new[]
        {
            MakeEvent(1, "run-A"),
            MakeEvent(2, "run-A"),
            MakeEvent(3, "run-A")
        };
        var store = BuildStoreMock(events);
        var tool = new ReadHistoryTool(store.Object);

        var parameters = new Dictionary<string, object?> { ["execution_run_id"] = "run-A" };
        var result = await tool.ExecuteAsync("query", parameters);

        result.Success.Should().BeTrue();
        using var doc = JsonDocument.Parse(result.Output!);
        doc.RootElement.GetArrayLength().Should().Be(3);
    }

    [Fact]
    public async Task Execute_WithSinceParameter_OnlyReturnsNewerEvents()
    {
        var events = Enumerable.Range(1, 5)
            .Select(i => MakeEvent(i, "run-B"))
            .ToArray();
        var store = BuildStoreMock(events);
        var tool = new ReadHistoryTool(store.Object);

        var parameters = new Dictionary<string, object?>
        {
            ["execution_run_id"] = "run-B",
            ["since"] = 3L
        };
        var result = await tool.ExecuteAsync("query", parameters);

        result.Success.Should().BeTrue();
        using var doc = JsonDocument.Parse(result.Output!);
        doc.RootElement.GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task Execute_ExceedsLimit_TruncatesToLimit()
    {
        var events = Enumerable.Range(1, 20)
            .Select(i => MakeEvent(i, "run-C"))
            .ToArray();
        var store = BuildStoreMock(events);
        var tool = new ReadHistoryTool(store.Object);

        var parameters = new Dictionary<string, object?>
        {
            ["execution_run_id"] = "run-C",
            ["limit"] = 5
        };
        var result = await tool.ExecuteAsync("query", parameters);

        result.Success.Should().BeTrue();
        using var doc = JsonDocument.Parse(result.Output!);
        doc.RootElement.GetArrayLength().Should().Be(5);
    }

    [Fact]
    public async Task Execute_InvalidRunId_ReturnsEmptyArray()
    {
        var store = BuildStoreMock(Array.Empty<AgentDecisionEvent>());
        var tool = new ReadHistoryTool(store.Object);

        var parameters = new Dictionary<string, object?> { ["execution_run_id"] = "nonexistent" };
        var result = await tool.ExecuteAsync("query", parameters);

        result.Success.Should().BeTrue();
        using var doc = JsonDocument.Parse(result.Output!);
        doc.RootElement.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Execute_MissingRunId_ReturnsEmptyArray()
    {
        var store = BuildStoreMock(Array.Empty<AgentDecisionEvent>());
        var tool = new ReadHistoryTool(store.Object);

        var parameters = new Dictionary<string, object?>();
        var result = await tool.ExecuteAsync("query", parameters);

        result.Success.Should().BeTrue();
        result.Output.Should().Be("[]");
    }

    [Fact]
    public async Task Execute_UnsupportedOperation_ReturnsFail()
    {
        var store = new Mock<IAgentHistoryStore>();
        var tool = new ReadHistoryTool(store.Object);

        var result = await tool.ExecuteAsync("delete", new Dictionary<string, object?>());

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("delete");
    }
}
