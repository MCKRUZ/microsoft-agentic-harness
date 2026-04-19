using Application.Common.Logging;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Application.Common.Tests.Logging;

/// <summary>
/// Tests for <see cref="LogEntryFactory"/> covering entry creation from logging scope.
/// </summary>
public sealed class LogEntryFactoryTests
{
    [Fact]
    public void CreateFromScope_NoScopeProvider_SetsBasicFields()
    {
        var entry = LogEntryFactory.CreateFromScope(
            LogLevel.Information,
            "TestCategory",
            new EventId(42, "TestEvent"),
            "Hello world",
            null,
            null);

        entry.Level.Should().Be(LogLevel.Information);
        entry.Category.Should().Be("TestCategory");
        entry.EventId.Id.Should().Be(42);
        entry.Message.Should().Be("Hello world");
        entry.Exception.Should().BeNull();
    }

    [Fact]
    public void CreateFromScope_SetsTimestamp()
    {
        var before = DateTimeOffset.UtcNow;

        var entry = LogEntryFactory.CreateFromScope(
            LogLevel.Debug, "Cat", default, "msg", null, null);

        entry.Timestamp.Should().BeOnOrAfter(before);
        entry.Timestamp.Should().BeOnOrBefore(DateTimeOffset.UtcNow);
    }

    [Fact]
    public void CreateFromScope_WithException_IncludesException()
    {
        var exception = new InvalidOperationException("test error");

        var entry = LogEntryFactory.CreateFromScope(
            LogLevel.Error, "Cat", default, "msg", exception, null);

        entry.Exception.Should().BeSameAs(exception);
    }

    [Fact]
    public void CreateFromScope_NoScope_ExecutorFieldsAreNull()
    {
        var entry = LogEntryFactory.CreateFromScope(
            LogLevel.Warning, "Cat", default, "msg", null, null);

        entry.ExecutorId.Should().BeNull();
        entry.ParentExecutorId.Should().BeNull();
        entry.CorrelationId.Should().BeNull();
        entry.StepNumber.Should().BeNull();
        entry.OperationName.Should().BeNull();
    }

    [Fact]
    public void CreateFromScope_WithScopeProvider_NoActiveScope_FieldsAreNull()
    {
        var scopeProvider = new Mock<IExternalScopeProvider>();
        scopeProvider.Setup(s => s.ForEachScope(
            It.IsAny<Action<object?, (string?, string?, string?, int?, string?)>>(),
            It.IsAny<(string?, string?, string?, int?, string?)>()));

        var entry = LogEntryFactory.CreateFromScope(
            LogLevel.Information, "Cat", default, "msg", null, scopeProvider.Object);

        entry.ExecutorId.Should().BeNull();
    }

    [Theory]
    [InlineData(LogLevel.Trace)]
    [InlineData(LogLevel.Debug)]
    [InlineData(LogLevel.Information)]
    [InlineData(LogLevel.Warning)]
    [InlineData(LogLevel.Error)]
    [InlineData(LogLevel.Critical)]
    public void CreateFromScope_AllLogLevels_SetsCorrectLevel(LogLevel level)
    {
        var entry = LogEntryFactory.CreateFromScope(level, "Cat", default, "msg", null, null);

        entry.Level.Should().Be(level);
    }
}
