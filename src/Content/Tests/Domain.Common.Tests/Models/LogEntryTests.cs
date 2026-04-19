using System.Collections.Immutable;
using Domain.Common.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Domain.Common.Tests.Models;

/// <summary>
/// Tests for <see cref="LogEntry"/> record — required properties, defaults,
/// and record behavior.
/// </summary>
public class LogEntryTests
{
    [Fact]
    public void Construction_WithRequiredProperties_Succeeds()
    {
        var entry = new LogEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            Level = LogLevel.Information,
            Category = "MyService",
            Message = "Something happened"
        };

        entry.Level.Should().Be(LogLevel.Information);
        entry.Category.Should().Be("MyService");
        entry.Message.Should().Be("Something happened");
    }

    [Fact]
    public void OptionalProperties_DefaultCorrectly()
    {
        var entry = new LogEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            Level = LogLevel.Debug,
            Category = "Test",
            Message = "msg"
        };

        entry.Exception.Should().BeNull();
        entry.ExecutorId.Should().BeNull();
        entry.ParentExecutorId.Should().BeNull();
        entry.CorrelationId.Should().BeNull();
        entry.StepNumber.Should().BeNull();
        entry.OperationName.Should().BeNull();
        entry.ScopeProperties.Should().BeEmpty();
    }

    [Fact]
    public void ScopeProperties_DefaultsToEmptyImmutableDictionary()
    {
        var entry = new LogEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            Level = LogLevel.Debug,
            Category = "Test",
            Message = "msg"
        };

        entry.ScopeProperties.Should().BeEmpty();
        entry.ScopeProperties.Should().BeAssignableTo<IReadOnlyDictionary<string, object?>>();
    }

    [Fact]
    public void Construction_WithAllProperties_SetsCorrectly()
    {
        var ex = new InvalidOperationException("fail");
        var props = new Dictionary<string, object?> { ["key"] = "value" }.ToImmutableDictionary();
        var entry = new LogEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            Level = LogLevel.Error,
            Category = "ErrorHandler",
            EventId = new EventId(42, "CustomEvent"),
            Message = "Error occurred",
            Exception = ex,
            ExecutorId = "agent-1",
            ParentExecutorId = "parent-1",
            CorrelationId = "corr-1",
            StepNumber = 3,
            OperationName = "tool-call",
            ScopeProperties = props
        };

        entry.EventId.Id.Should().Be(42);
        entry.Exception.Should().BeSameAs(ex);
        entry.ExecutorId.Should().Be("agent-1");
        entry.ScopeProperties.Should().ContainKey("key");
    }

    [Fact]
    public void WithExpression_DoesNotMutateOriginal()
    {
        var original = new LogEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            Level = LogLevel.Information,
            Category = "Test",
            Message = "original"
        };

        var modified = original with { Message = "modified" };

        original.Message.Should().Be("original");
        modified.Message.Should().Be("modified");
    }
}
