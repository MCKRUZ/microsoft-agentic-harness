using Application.Common.Logging;
using Domain.Common.Config;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.Common.Tests.Logging;

/// <summary>
/// Integration tests for <see cref="NamedPipeLoggerProvider"/> covering
/// lifecycle management, logger creation, message queuing, and dispose behavior.
/// </summary>
public sealed class NamedPipeLoggerProviderTests
{
    private static IOptionsMonitor<LoggingConfig> CreateConfig(string pipeName = "test-pipe")
    {
        var mock = new Mock<IOptionsMonitor<LoggingConfig>>();
        mock.Setup(m => m.CurrentValue).Returns(new LoggingConfig { PipeName = pipeName });
        return mock.Object;
    }

    [Fact]
    public void CreateLogger_ReturnsNonNullLogger()
    {
        using var provider = new NamedPipeLoggerProvider(CreateConfig());

        var logger = provider.CreateLogger("TestCategory");

        logger.Should().NotBeNull();
    }

    [Fact]
    public void CreateLogger_SameCategory_ReturnsSameInstance()
    {
        using var provider = new NamedPipeLoggerProvider(CreateConfig());

        var logger1 = provider.CreateLogger("TestCategory");
        var logger2 = provider.CreateLogger("TestCategory");

        logger1.Should().BeSameAs(logger2);
    }

    [Fact]
    public void CreateLogger_DifferentCategories_ReturnsDifferentInstances()
    {
        using var provider = new NamedPipeLoggerProvider(CreateConfig());

        var logger1 = provider.CreateLogger("Category.A");
        var logger2 = provider.CreateLogger("Category.B");

        logger1.Should().NotBeSameAs(logger2);
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var provider = new NamedPipeLoggerProvider(CreateConfig());

        var act = () => provider.Dispose();

        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_CalledMultipleTimes_DoesNotThrow()
    {
        var provider = new NamedPipeLoggerProvider(CreateConfig());

        var act = () =>
        {
            provider.Dispose();
            provider.Dispose();
        };

        act.Should().NotThrow();
    }

    [Fact]
    public void CreateLogger_WithScopeProvider_DoesNotThrow()
    {
        var scopeProvider = new Mock<IExternalScopeProvider>();
        using var provider = new NamedPipeLoggerProvider(CreateConfig(), scopeProvider.Object);

        var logger = provider.CreateLogger("ScopedCategory");

        logger.Should().NotBeNull();
    }

    [Fact]
    public void WriteMessage_NonBlocking_DoesNotThrow()
    {
        using var provider = new NamedPipeLoggerProvider(CreateConfig());

        // WriteMessage is internal but we can test via logger.Log
        var logger = provider.CreateLogger("TestCategory");
        var act = () => logger.LogInformation("Test message");

        act.Should().NotThrow();
    }

    [Fact]
    public void CreateLogger_AfterDispose_StillReturnsInstance()
    {
        // Logger provider doesn't throw on CreateLogger after dispose;
        // the loggers just silently fail to write
        var provider = new NamedPipeLoggerProvider(CreateConfig());
        provider.Dispose();

        var act = () => provider.CreateLogger("PostDispose");

        act.Should().NotThrow();
    }
}
