using Application.AI.Common.Interfaces.Memory;
using Domain.AI.Models;
using FluentAssertions;
using Infrastructure.AI.Tools;
using Moq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Xunit;

namespace Infrastructure.AI.Tests.Tools;

/// <summary>
/// Tests for <see cref="ReadHistoryTool"/> covering parameter parsing,
/// unsupported operations, empty/missing run IDs, and query delegation.
/// </summary>
public sealed class ReadHistoryToolTests
{
    private readonly Mock<IAgentHistoryStore> _historyStore;
    private readonly ReadHistoryTool _sut;

    public ReadHistoryToolTests()
    {
        _historyStore = new Mock<IAgentHistoryStore>();
        _sut = new ReadHistoryTool(_historyStore.Object);
    }

    [Fact]
    public void ToolProperties_AreCorrect()
    {
        _sut.Name.Should().Be("read_history");
        _sut.IsReadOnly.Should().BeTrue();
        _sut.IsConcurrencySafe.Should().BeTrue();
        _sut.SupportedOperations.Should().Contain("query");
    }

    [Fact]
    public async Task Execute_UnsupportedOperation_ReturnsFail()
    {
        var result = await _sut.ExecuteAsync("delete",
            new Dictionary<string, object?>());

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("does not support operation");
    }

    [Fact]
    public async Task Execute_MissingRunId_ReturnsEmptyArray()
    {
        var result = await _sut.ExecuteAsync("query",
            new Dictionary<string, object?>());

        result.Success.Should().BeTrue();
        result.Output.Should().Be("[]");
    }

    [Fact]
    public async Task Execute_EmptyRunId_ReturnsEmptyArray()
    {
        var result = await _sut.ExecuteAsync("query",
            new Dictionary<string, object?> { ["execution_run_id"] = "" });

        result.Success.Should().BeTrue();
        result.Output.Should().Be("[]");
    }

    [Fact]
    public async Task Execute_NullRunId_ReturnsEmptyArray()
    {
        var result = await _sut.ExecuteAsync("query",
            new Dictionary<string, object?> { ["execution_run_id"] = null });

        result.Success.Should().BeTrue();
        result.Output.Should().Be("[]");
    }

    [Fact]
    public async Task Execute_ValidRunId_DelegatesToStore()
    {
        var events = new List<AgentDecisionEvent>
        {
            new()
            {
                Sequence = 1,
                Timestamp = DateTimeOffset.UtcNow,
                EventType = "tool_call",
                ExecutionRunId = "run-1",
                TurnId = "turn-1",
                ToolName = "file_read"
            }
        };

        _historyStore.Setup(s => s.QueryAsync(It.IsAny<DecisionLogQuery>(), It.IsAny<CancellationToken>()))
            .Returns(ToAsyncEnumerable(events));

        var result = await _sut.ExecuteAsync("query",
            new Dictionary<string, object?> { ["execution_run_id"] = "run-1" });

        result.Success.Should().BeTrue();
        var parsed = JsonDocument.Parse(result.Output!);
        parsed.RootElement.GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Execute_WithFilters_PassesQueryParameters()
    {
        DecisionLogQuery? capturedQuery = null;
        _historyStore.Setup(s => s.QueryAsync(It.IsAny<DecisionLogQuery>(), It.IsAny<CancellationToken>()))
            .Callback<DecisionLogQuery, CancellationToken>((q, _) => capturedQuery = q)
            .Returns(ToAsyncEnumerable([]));

        await _sut.ExecuteAsync("query", new Dictionary<string, object?>
        {
            ["execution_run_id"] = "run-2",
            ["event_type"] = "tool_result",
            ["tool_name"] = "file_read",
            ["since"] = "50",
            ["limit"] = "25"
        });

        capturedQuery.Should().NotBeNull();
        capturedQuery!.ExecutionRunId.Should().Be("run-2");
        capturedQuery.EventType.Should().Be("tool_result");
        capturedQuery.ToolName.Should().Be("file_read");
        capturedQuery.Since.Should().Be(50);
        capturedQuery.Limit.Should().Be(25);
    }

    [Fact]
    public async Task Execute_LongSinceValue_ParsesCorrectly()
    {
        DecisionLogQuery? capturedQuery = null;
        _historyStore.Setup(s => s.QueryAsync(It.IsAny<DecisionLogQuery>(), It.IsAny<CancellationToken>()))
            .Callback<DecisionLogQuery, CancellationToken>((q, _) => capturedQuery = q)
            .Returns(ToAsyncEnumerable([]));

        await _sut.ExecuteAsync("query", new Dictionary<string, object?>
        {
            ["execution_run_id"] = "run-3",
            ["since"] = 999L
        });

        capturedQuery!.Since.Should().Be(999);
    }

    [Fact]
    public async Task Execute_IntLimitValue_ParsesCorrectly()
    {
        DecisionLogQuery? capturedQuery = null;
        _historyStore.Setup(s => s.QueryAsync(It.IsAny<DecisionLogQuery>(), It.IsAny<CancellationToken>()))
            .Callback<DecisionLogQuery, CancellationToken>((q, _) => capturedQuery = q)
            .Returns(ToAsyncEnumerable([]));

        await _sut.ExecuteAsync("query", new Dictionary<string, object?>
        {
            ["execution_run_id"] = "run-4",
            ["limit"] = 50
        });

        capturedQuery!.Limit.Should().Be(50);
    }

    [Fact]
    public async Task Execute_DefaultLimit_Is100()
    {
        DecisionLogQuery? capturedQuery = null;
        _historyStore.Setup(s => s.QueryAsync(It.IsAny<DecisionLogQuery>(), It.IsAny<CancellationToken>()))
            .Callback<DecisionLogQuery, CancellationToken>((q, _) => capturedQuery = q)
            .Returns(ToAsyncEnumerable([]));

        await _sut.ExecuteAsync("query", new Dictionary<string, object?>
        {
            ["execution_run_id"] = "run-5"
        });

        capturedQuery!.Limit.Should().Be(100);
    }

    [Fact]
    public async Task Execute_InvalidSinceValue_DefaultsToZero()
    {
        DecisionLogQuery? capturedQuery = null;
        _historyStore.Setup(s => s.QueryAsync(It.IsAny<DecisionLogQuery>(), It.IsAny<CancellationToken>()))
            .Callback<DecisionLogQuery, CancellationToken>((q, _) => capturedQuery = q)
            .Returns(ToAsyncEnumerable([]));

        await _sut.ExecuteAsync("query", new Dictionary<string, object?>
        {
            ["execution_run_id"] = "run-6",
            ["since"] = "not-a-number"
        });

        capturedQuery!.Since.Should().Be(0);
    }

    [Fact]
    public async Task Execute_InvalidLimitValue_DefaultsTo100()
    {
        DecisionLogQuery? capturedQuery = null;
        _historyStore.Setup(s => s.QueryAsync(It.IsAny<DecisionLogQuery>(), It.IsAny<CancellationToken>()))
            .Callback<DecisionLogQuery, CancellationToken>((q, _) => capturedQuery = q)
            .Returns(ToAsyncEnumerable([]));

        await _sut.ExecuteAsync("query", new Dictionary<string, object?>
        {
            ["execution_run_id"] = "run-7",
            ["limit"] = "invalid"
        });

        capturedQuery!.Limit.Should().Be(100);
    }

    private static async IAsyncEnumerable<AgentDecisionEvent> ToAsyncEnumerable(
        IEnumerable<AgentDecisionEvent> events,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var evt in events)
        {
            ct.ThrowIfCancellationRequested();
            yield return evt;
            await Task.CompletedTask;
        }
    }
}
