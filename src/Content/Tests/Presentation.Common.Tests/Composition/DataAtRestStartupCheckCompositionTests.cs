using FluentAssertions;
using Infrastructure.AI.DataAtRest;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tests.Common;
using Xunit;

namespace Presentation.Common.Tests.Composition;

/// <summary>
/// <see cref="DataAtRestStartupCheck"/> (#904) through the real composition root: every host starts it, and it reads the
/// deployer's statement from the configuration the host binds, not from an object a test built.
/// </summary>
public sealed class DataAtRestStartupCheckCompositionTests
{
    [Fact]
    public void GetServices_AnyHost_RegistersDataAtRestCheck()
    {
        var (services, _) = CompositionRootTestHost.ComposeServices([]);

        services.Should().Contain(d => d.ServiceType == typeof(IHostedService)
            && d.ImplementationType == typeof(DataAtRestStartupCheck));
    }

    [Fact]
    public async Task StartAsync_StatementInBoundConfiguration_LogsVolumeIsEncrypted()
    {
        await using var provider = CompositionRootTestHost.BuildProvider(
            [new("AppConfig:Infrastructure:DataAtRest:Encryption", "Volume")]);
        var logger = new CapturingLogger<DataAtRestStartupCheck>();

        await ActivatorUtilities.CreateInstance<DataAtRestStartupCheck>(provider, logger)
            .StartAsync(CancellationToken.None);

        logger.Logged(LogLevel.Information, "is encrypted")
            .Should().BeTrue("the setting reaches the check through the host's own binding");
        logger.Logged(LogLevel.Warning, string.Empty).Should().BeFalse();
    }
}
