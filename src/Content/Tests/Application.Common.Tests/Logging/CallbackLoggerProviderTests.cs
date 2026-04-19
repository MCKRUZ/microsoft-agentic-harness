using Application.Common.Logging;
using Domain.Common.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Application.Common.Tests.Logging;

public sealed class CallbackLoggerProviderTests : IDisposable
{
    private readonly List<LogEntry> _captured = [];
    private readonly CallbackLoggerProvider _provider;

    public CallbackLoggerProviderTests()
    {
        _provider = new CallbackLoggerProvider(entry => _captured.Add(entry));
    }

    public void Dispose() => _provider.Dispose();

    [Fact]
    public void CreateLogger_ReturnsSameInstanceForSameCategory()
    {
        var logger1 = _provider.CreateLogger("CategoryA");
        var logger2 = _provider.CreateLogger("CategoryA");

        logger1.Should().BeSameAs(logger2);
    }

    [Fact]
    public void CreateLogger_ReturnsDifferentInstancesForDifferentCategories()
    {
        var logger1 = _provider.CreateLogger("CategoryA");
        var logger2 = _provider.CreateLogger("CategoryB");

        logger1.Should().NotBeSameAs(logger2);
    }

    [Fact]
    public void Constructor_NullCallback_ThrowsArgumentNullException()
    {
        var act = () => new CallbackLoggerProvider(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Log_InvokesCallbackWithCorrectEntry()
    {
        var logger = _provider.CreateLogger("TestCategory");

        logger.LogWarning("Warning message");

        _captured.Should().ContainSingle();
        _captured[0].Level.Should().Be(LogLevel.Warning);
        _captured[0].Message.Should().Be("Warning message");
        _captured[0].Category.Should().Be("TestCategory");
    }

    [Fact]
    public void Log_LogLevelNone_DoesNotInvokeCallback()
    {
        var logger = _provider.CreateLogger("TestCategory");

        logger.Log(LogLevel.None, "Should not appear");

        _captured.Should().BeEmpty();
    }

    [Fact]
    public void Log_CallbackThrows_DoesNotPropagate()
    {
        using var provider = new CallbackLoggerProvider(_ => throw new InvalidOperationException("Boom"));
        var logger = provider.CreateLogger("TestCategory");

        var act = () => logger.LogInformation("Should not throw");

        act.Should().NotThrow();
    }

    [Fact]
    public void Log_WithException_CapturesExceptionInEntry()
    {
        var logger = _provider.CreateLogger("TestCategory");
        var ex = new ArgumentException("Bad arg");

        logger.LogError(ex, "Error with exception");

        _captured.Should().ContainSingle();
        _captured[0].Exception.Should().BeSameAs(ex);
    }

    [Fact]
    public void Log_MultipleMessages_AllCaptured()
    {
        var logger = _provider.CreateLogger("TestCategory");

        logger.LogDebug("Debug msg");
        logger.LogInformation("Info msg");
        logger.LogCritical("Critical msg");

        _captured.Should().HaveCount(3);
        _captured[0].Level.Should().Be(LogLevel.Debug);
        _captured[1].Level.Should().Be(LogLevel.Information);
        _captured[2].Level.Should().Be(LogLevel.Critical);
    }
}
