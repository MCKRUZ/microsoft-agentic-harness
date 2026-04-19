using Application.Common.Logging;
using Domain.Common.Config;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.Common.Tests.Logging;

public sealed class InMemoryRingBufferLoggerProviderTests : IDisposable
{
    private readonly InMemoryRingBufferLoggerProvider _provider;

    public InMemoryRingBufferLoggerProviderTests()
    {
        var config = Mock.Of<IOptionsMonitor<LoggingConfig>>(m =>
            m.CurrentValue == new LoggingConfig { RingBufferCapacity = 10 });
        _provider = new InMemoryRingBufferLoggerProvider(config);
    }

    public void Dispose() => _provider.Dispose();

    [Fact]
    public void CreateLogger_ReturnsSameInstanceForSameCategory()
    {
        var logger1 = _provider.CreateLogger("TestCategory");
        var logger2 = _provider.CreateLogger("TestCategory");

        logger1.Should().BeSameAs(logger2);
    }

    [Fact]
    public void CreateLogger_ReturnsDifferentInstancesForDifferentCategories()
    {
        var logger1 = _provider.CreateLogger("Category.A");
        var logger2 = _provider.CreateLogger("Category.B");

        logger1.Should().NotBeSameAs(logger2);
    }

    [Fact]
    public void Log_AddsEntryToBuffer()
    {
        var logger = _provider.CreateLogger("TestCategory");

        logger.LogInformation("Test message");

        var entries = _provider.GetEntries();
        entries.Should().ContainSingle();
        entries[0].Message.Should().Be("Test message");
        entries[0].Level.Should().Be(LogLevel.Information);
        entries[0].Category.Should().Be("TestCategory");
    }

    [Fact]
    public void Log_MultipleEntries_ReturnsInOrder()
    {
        var logger = _provider.CreateLogger("TestCategory");

        logger.LogInformation("First");
        logger.LogWarning("Second");
        logger.LogError("Third");

        var entries = _provider.GetEntries();
        entries.Should().HaveCount(3);
        entries[0].Message.Should().Be("First");
        entries[1].Message.Should().Be("Second");
        entries[2].Message.Should().Be("Third");
    }

    [Fact]
    public void Log_ExceedsCapacity_OverwritesOldestEntries()
    {
        var logger = _provider.CreateLogger("TestCategory");

        // Buffer capacity is 10, write 15 entries
        for (var i = 0; i < 15; i++)
            logger.LogInformation($"Entry-{i}");

        var entries = _provider.GetEntries();
        entries.Should().HaveCount(10);
        // Oldest 5 entries (0-4) should be overwritten
        entries[0].Message.Should().Be("Entry-5");
        entries[9].Message.Should().Be("Entry-14");
    }

    [Fact]
    public void Log_LogLevelNone_DoesNotAddEntry()
    {
        var logger = _provider.CreateLogger("TestCategory");

        logger.Log(LogLevel.None, "Should not appear");

        _provider.GetEntries().Should().BeEmpty();
    }

    [Fact]
    public void Clear_RemovesAllEntries()
    {
        var logger = _provider.CreateLogger("TestCategory");
        logger.LogInformation("Entry 1");
        logger.LogInformation("Entry 2");

        _provider.Clear();

        _provider.GetEntries().Should().BeEmpty();
    }

    [Fact]
    public void GetEntries_AfterClear_AcceptsNewEntries()
    {
        var logger = _provider.CreateLogger("TestCategory");
        logger.LogInformation("Before clear");

        _provider.Clear();

        logger.LogInformation("After clear");

        var entries = _provider.GetEntries();
        entries.Should().ContainSingle();
        entries[0].Message.Should().Be("After clear");
    }

    [Fact]
    public void Constructor_CapacityBelowMinimum_ClampsToTen()
    {
        var config = Mock.Of<IOptionsMonitor<LoggingConfig>>(m =>
            m.CurrentValue == new LoggingConfig { RingBufferCapacity = 3 });
        using var provider = new InMemoryRingBufferLoggerProvider(config);
        var logger = provider.CreateLogger("Test");

        // Write 12 entries; if capacity were 3 we'd only get 3 back.
        // With min clamp of 10, we should get 10 back.
        for (var i = 0; i < 12; i++)
            logger.LogInformation($"Entry-{i}");

        var entries = provider.GetEntries();
        entries.Should().HaveCount(10);
    }

    [Fact]
    public void Log_WithException_CapturesExceptionInEntry()
    {
        var logger = _provider.CreateLogger("TestCategory");
        var ex = new InvalidOperationException("Something broke");

        logger.LogError(ex, "Error occurred");

        var entries = _provider.GetEntries();
        entries.Should().ContainSingle();
        entries[0].Exception.Should().BeSameAs(ex);
        entries[0].Level.Should().Be(LogLevel.Error);
    }

    [Fact]
    public void Log_WithEventId_CapturesEventIdInEntry()
    {
        var logger = _provider.CreateLogger("TestCategory");
        var eventId = new EventId(42, "TestEvent");

        logger.Log(LogLevel.Information, eventId, "Event message", null, (s, _) => s);

        var entries = _provider.GetEntries();
        entries.Should().ContainSingle();
        entries[0].EventId.Should().Be(eventId);
    }
}
