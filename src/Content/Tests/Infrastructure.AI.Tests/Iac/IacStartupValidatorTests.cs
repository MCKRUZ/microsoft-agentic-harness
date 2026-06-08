using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Iac;
using FluentAssertions;
using Infrastructure.AI.Iac;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Iac;

/// <summary>
/// Tests for <see cref="IacStartupValidator"/>. Disabled is always a no-op; each
/// misconfiguration throws <see cref="InvalidOperationException"/> at boot; a valid
/// config passes.
/// </summary>
public sealed class IacStartupValidatorTests
{
    private static IacStartupValidator Create(IacConfig iac)
    {
        var appConfig = new AppConfig { AI = new AIConfig { Iac = iac } };
        var monitor = Mock.Of<IOptionsMonitor<AppConfig>>(o => o.CurrentValue == appConfig);
        return new IacStartupValidator(monitor, NullLogger<IacStartupValidator>.Instance);
    }

    private static IacConfig ValidConfig() => new()
    {
        Enabled = true,
        EnabledBackends = ["terraform", "bicep"],
        TerraformVersion = "1.9.5",
        BicepVersion = "0.30.23",
        CheckovVersion = "3.2.0",
        TfsecVersion = "1.28.11",
        ArmTtkVersion = "0.24",
        BlockingSeverity = "High",
        RegistryAllowlist = ["registry.terraform.io"]
    };

    [Fact]
    public async Task StartAsync_Disabled_NoOp()
    {
        var config = ValidConfig();
        config.Enabled = false;
        config.EnabledBackends = []; // would be invalid if enabled
        var sut = Create(config);

        var act = () => sut.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StartAsync_Valid_Passes()
    {
        var sut = Create(ValidConfig());

        var act = () => sut.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StartAsync_EmptyBackends_Throws()
    {
        var config = ValidConfig();
        config.EnabledBackends = [];
        var sut = Create(config);

        var act = () => sut.StartAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*EnabledBackends*");
    }

    [Fact]
    public async Task StartAsync_UnknownBackend_Throws()
    {
        var config = ValidConfig();
        config.EnabledBackends = ["terraform", "pulumi"];
        var sut = Create(config);

        var act = () => sut.StartAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*unknown backend*");
    }

    [Fact]
    public async Task StartAsync_TerraformEnabledButTerraformVersionBlank_Throws()
    {
        var config = ValidConfig();
        config.TerraformVersion = "";
        var sut = Create(config);

        var act = () => sut.StartAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*TerraformVersion*");
    }

    [Fact]
    public async Task StartAsync_BicepEnabledButArmTtkVersionBlank_Throws()
    {
        var config = ValidConfig();
        config.ArmTtkVersion = "";
        var sut = Create(config);

        var act = () => sut.StartAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*ArmTtkVersion*");
    }

    [Fact]
    public async Task StartAsync_OnlyBicepEnabled_DoesNotRequireTerraformVersion()
    {
        var config = ValidConfig();
        config.EnabledBackends = ["bicep"];
        config.TerraformVersion = ""; // not needed when terraform is not enabled
        var sut = Create(config);

        var act = () => sut.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StartAsync_InvalidBlockingSeverity_Throws()
    {
        var config = ValidConfig();
        config.BlockingSeverity = "nonsense";
        var sut = Create(config);

        var act = () => sut.StartAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*BlockingSeverity*");
    }

    [Fact]
    public async Task StartAsync_EmptyRegistryAllowlist_Throws()
    {
        var config = ValidConfig();
        config.RegistryAllowlist = [];
        var sut = Create(config);

        var act = () => sut.StartAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*RegistryAllowlist*");
    }
}
