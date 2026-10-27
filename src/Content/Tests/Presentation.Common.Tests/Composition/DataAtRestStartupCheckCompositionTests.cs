using FluentAssertions;
using Infrastructure.AI.DataAtRest;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Presentation.Common.Tests.Composition;

/// <summary>
/// <see cref="DataAtRestStartupCheck"/> (#904) through the real composition root: every host starts it, and it reads the
/// deployer's statement from the configuration the host binds, not from an object a test built.
/// </summary>
public sealed class DataAtRestStartupCheckCompositionTests
{
    [Fact]
    public void EveryHost_StartsTheCheck()
    {
        var (services, _) = CompositionRootTestHost.ComposeServices([]);

        services.Should().Contain(d => d.ServiceType == typeof(IHostedService)
            && d.ImplementationType == typeof(DataAtRestStartupCheck));
    }

    [Fact]
    public async Task TheCheck_ReadsTheStatementFromBoundConfiguration()
    {
        await using var provider = CompositionRootTestHost.BuildProvider(
            [new("AppConfig:Infrastructure:DataAtRest:Encryption", "Volume")]);
        var logger = new Mock<ILogger<DataAtRestStartupCheck>>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        await ActivatorUtilities.CreateInstance<DataAtRestStartupCheck>(provider, logger.Object)
            .StartAsync(CancellationToken.None);

        logger.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("is encrypted")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once, "the setting reaches the check through the host's own binding");
        logger.Verify(
            l => l.Log(LogLevel.Warning, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }
}
