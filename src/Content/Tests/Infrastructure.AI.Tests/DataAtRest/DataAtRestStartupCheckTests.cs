using Domain.Common.Config;
using Domain.Common.Config.Infrastructure;
using FluentAssertions;
using Infrastructure.AI.DataAtRest;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Tests.Common;
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
    public async Task StartAsync_EncryptionStatement_WarnsUnlessVolumeIsEncrypted(
        DataAtRestEncryption encryption, string? environment, LogLevel? expected)
    {
        // No host environment at all (a composition without a host) is not Development, so it warns. A value outside the
        // enum is no statement at all, so it warns too.
        var config = new AppConfig();
        config.Infrastructure.DataAtRest.Encryption = encryption;
        var logger = new CapturingLogger<DataAtRestStartupCheck>();
        var check = new DataAtRestStartupCheck(
            Options.Create(config),
            logger,
            environment is null ? null : Mock.Of<IHostEnvironment>(e => e.EnvironmentName == environment));

        await check.StartAsync(CancellationToken.None);

        if (expected is null)
            logger.Messages.Should().BeEmpty();
        else
        {
            logger.Messages.Should().ContainSingle();
            logger.Logged(expected.Value, string.Empty).Should().BeTrue();
        }
    }

    [Fact]
    public async Task StartAsync_NoStatement_SaysFilesAreNotEncryptedAndWhatToSet()
    {
        var logger = new CapturingLogger<DataAtRestStartupCheck>();

        await new DataAtRestStartupCheck(Options.Create(new AppConfig()), logger).StartAsync(CancellationToken.None);

        logger.Messages.Single().Should().Contain("does not encrypt")
            .And.Contain("AppConfig:Infrastructure:DataAtRest:Encryption");
    }
}
