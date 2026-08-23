using Application.Common.Logging;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Application.Common.Tests.Logging;

/// <summary>Tests for <see cref="RedactingLoggerFactory"/> (#457).</summary>
public sealed class RedactingLoggerFactoryTests
{
    [Fact]
    public void CreateLogger_ReturnsARedactingLoggerWrappingTheInnerFactorysLogger()
    {
        var innerLogger = new Mock<ILogger>();
        var innerFactory = new Mock<ILoggerFactory>();
        innerFactory.Setup(f => f.CreateLogger("category")).Returns(innerLogger.Object);
        var redactor = Mock.Of<ILocalLogRedactor>(r => r.Enabled == false);

        var factory = new RedactingLoggerFactory(innerFactory.Object, redactor);
        var logger = factory.CreateLogger("category");

        logger.Should().BeOfType<RedactingLogger>();
        innerFactory.Verify(f => f.CreateLogger("category"), Times.Once);
    }

    [Fact]
    public void AddProvider_ForwardsToInnerFactory()
    {
        var innerFactory = new Mock<ILoggerFactory>();
        var provider = Mock.Of<ILoggerProvider>();
        var factory = new RedactingLoggerFactory(innerFactory.Object, Mock.Of<ILocalLogRedactor>());

        factory.AddProvider(provider);

        innerFactory.Verify(f => f.AddProvider(provider), Times.Once);
    }

    [Fact]
    public void Dispose_DisposesInnerFactory()
    {
        var innerFactory = new Mock<ILoggerFactory>();
        var factory = new RedactingLoggerFactory(innerFactory.Object, Mock.Of<ILocalLogRedactor>());

        factory.Dispose();

        innerFactory.Verify(f => f.Dispose(), Times.Once);
    }
}
