using Domain.Common.Config;
using Domain.Common.Config.Infrastructure;
using FluentAssertions;
using Infrastructure.AI.DataAtRest;
using Infrastructure.AI.Tests.Resilience;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.DataAtRest;

/// <summary>
/// <see cref="DataAtRestStartupCheck"/> (#904): a host outside Development that has not said how its local files are
/// protected is told they are not encrypted by the harness, and a host that has said so is not warned.
/// </summary>
public sealed class DataAtRestStartupCheckTests
{
    [Theory]
    [InlineData(DataAtRestEncryption.Unspecified, "Production", LogLevel.Warning)]
    [InlineData(DataAtRestEncryption.Unspecified, null, LogLevel.Warning)]
    [InlineData(DataAtRestEncryption.Unspecified, "Development", null)]
    [InlineData(DataAtRestEncryption.Volume, "Production", LogLevel.Information)]
    [InlineData((DataAtRestEncryption)7, "Production", LogLevel.Warning)]
    public async Task StartAsync_WarnsUnlessTheDeployerStatedTheVolumeIsEncrypted(
        DataAtRestEncryption encryption, string? environment, LogLevel? expected)
    {
        // No host environment at all (a composition without a host) is not Development, so it warns. A value outside the
        // enum is no statement at all, so it warns too.
        var config = new AppConfig();
        config.Infrastructure.DataAtRest.Encryption = encryption;
        var logger = new RecordingLogger<DataAtRestStartupCheck>();
        var check = new DataAtRestStartupCheck(
            Options.Create(config),
            logger,
            environment is null ? null : Mock.Of<IHostEnvironment>(e => e.EnvironmentName == environment));

        await check.StartAsync(CancellationToken.None);

        if (expected is null)
            logger.Entries.Should().BeEmpty();
        else
            logger.Entries.Should().ContainSingle().Which.Level.Should().Be(expected.Value);
    }

    [Fact]
    public async Task TheWarning_SaysTheFilesAreNotEncryptedAndWhatToSet()
    {
        var logger = new RecordingLogger<DataAtRestStartupCheck>();

        await new DataAtRestStartupCheck(Options.Create(new AppConfig()), logger).StartAsync(CancellationToken.None);

        logger.Entries.Single().Message.Should().Contain("does not encrypt")
            .And.Contain("AppConfig:Infrastructure:DataAtRest:Encryption");
    }
}
