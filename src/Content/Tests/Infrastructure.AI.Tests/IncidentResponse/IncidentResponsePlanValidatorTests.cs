using Domain.Common.Config;
using Domain.Common.Config.AI.IncidentResponse;
using FluentAssertions;
using Infrastructure.AI.IncidentResponse;
using Infrastructure.AI.Tests.Changes.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.IncidentResponse;

/// <summary>
/// Boot-time validator tests for <see cref="IncidentResponsePlanValidator"/>.
/// </summary>
public sealed class IncidentResponsePlanValidatorTests
{
    private static IncidentResponsePlanValidator NewSut(AppConfig cfg)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        return new(
            services,
            new TestConfig.StaticOptionsMonitor<AppConfig>(cfg),
            NullLogger<IncidentResponsePlanValidator>.Instance);
    }

    [Fact]
    public async Task StartAsync_EmptyRegistry_NoOps()
    {
        var cfg = new AppConfig();
        var sut = NewSut(cfg);

        await sut.Invoking(s => s.StartAsync(CancellationToken.None))
            .Should().NotThrowAsync();
    }

    [Fact]
    public async Task StartAsync_ValidConfig_AllowsBoot()
    {
        var cfg = new AppConfig();
        cfg.AI.IncidentResponse.Plans =
        [
            new() { Name = "DataExfil", IncidentType = "DataExfiltrationSuspected", AutonomyTierOverride = "Restricted", AdditionalRequiredGates = ["compliance"] },
            new() { Name = "Rollback", IncidentType = "ProductionRollback", AutonomyTierOverride = "Supervised" }
        ];
        cfg.AI.IncidentResponse.DefaultPlanName = "DataExfil";
        var sut = NewSut(cfg);

        await sut.Invoking(s => s.StartAsync(CancellationToken.None))
            .Should().NotThrowAsync();
    }

    [Fact]
    public async Task StartAsync_UnknownAutonomyTier_Throws()
    {
        var cfg = new AppConfig();
        cfg.AI.IncidentResponse.Plans =
        [
            new() { Name = "Bogus", IncidentType = "Whatever", AutonomyTierOverride = "Godmode" }
        ];
        var sut = NewSut(cfg);

        var ex = await sut.Invoking(s => s.StartAsync(CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain("AutonomyTierOverride");
        ex.Which.Message.Should().Contain("Godmode");
        ex.Which.Message.Should().Contain("Restricted"); // expected to list known tiers
    }

    [Theory]
    [InlineData("2")]                       // the numeric form of Autonomous
    [InlineData(" 2")]                      // and behind a stray space
    [InlineData("99")]                      // outside the defined range
    [InlineData("Restricted,Autonomous")]   // comma-composite, OR'd to Autonomous
    public async Task StartAsync_NonNameAutonomyTier_Throws(string tier)
    {
        // #300. StartAsync_UnknownAutonomyTier_Throws above proves a nonsense NAME is refused, but a
        // bare Enum.TryParse accepted every value here — so the one boot check standing between a
        // typo and a widened autonomy tier passed exactly the forms most likely to widen it.
        var cfg = new AppConfig();
        cfg.AI.IncidentResponse.Plans =
        [
            new() { Name = "Bogus", IncidentType = "Whatever", AutonomyTierOverride = tier }
        ];
        var sut = NewSut(cfg);

        var ex = await sut.Invoking(s => s.StartAsync(CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain("AutonomyTierOverride");
    }

    [Theory]
    [InlineData("restricted")]
    [InlineData("SUPERVISED")]
    public async Task StartAsync_DifferentlyCasedAutonomyTier_AllowsBoot(string tier)
    {
        // The switch to the shared reader also made this case-insensitive, where it was previously
        // case-sensitive. Pinned deliberately rather than left implicit: AutonomyLevel is read
        // case-insensitively everywhere else, and a config value must not mean different things to
        // different readers.
        var cfg = new AppConfig();
        cfg.AI.IncidentResponse.Plans =
        [
            new() { Name = "Plan", IncidentType = "Whatever", AutonomyTierOverride = tier }
        ];
        var sut = NewSut(cfg);

        await sut.Invoking(s => s.StartAsync(CancellationToken.None))
            .Should().NotThrowAsync();
    }

    [Fact]
    public async Task StartAsync_DuplicatePlanNames_Throws()
    {
        var cfg = new AppConfig();
        cfg.AI.IncidentResponse.Plans =
        [
            new() { Name = "Same", IncidentType = "TypeA" },
            new() { Name = "same", IncidentType = "TypeB" } // case-insensitive duplicate
        ];
        var sut = NewSut(cfg);

        var ex = await sut.Invoking(s => s.StartAsync(CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain("duplicate");
        ex.Which.Message.Should().Contain("Name");
    }

    [Fact]
    public async Task StartAsync_DuplicateIncidentTypes_Throws()
    {
        var cfg = new AppConfig();
        cfg.AI.IncidentResponse.Plans =
        [
            new() { Name = "First", IncidentType = "Same" },
            new() { Name = "Second", IncidentType = "same" } // case-insensitive duplicate
        ];
        var sut = NewSut(cfg);

        var ex = await sut.Invoking(s => s.StartAsync(CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain("duplicate");
        ex.Which.Message.Should().Contain("IncidentType");
    }

    [Fact]
    public async Task StartAsync_DefaultPlanNameDoesNotMatchAnyPlan_Throws()
    {
        var cfg = new AppConfig();
        cfg.AI.IncidentResponse.Plans = [new() { Name = "Plan1", IncidentType = "Type1" }];
        cfg.AI.IncidentResponse.DefaultPlanName = "MissingPlan";
        var sut = NewSut(cfg);

        var ex = await sut.Invoking(s => s.StartAsync(CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain("DefaultPlanName");
        ex.Which.Message.Should().Contain("MissingPlan");
    }

    [Fact]
    public async Task StartAsync_DefaultPlanNameWithEmptyRegistry_Throws()
    {
        var cfg = new AppConfig();
        cfg.AI.IncidentResponse.DefaultPlanName = "SomePlan";
        var sut = NewSut(cfg);

        var ex = await sut.Invoking(s => s.StartAsync(CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain("DefaultPlanName");
        ex.Which.Message.Should().Contain("no plans");
    }

    [Fact]
    public async Task StartAsync_MissingName_Throws()
    {
        var cfg = new AppConfig();
        cfg.AI.IncidentResponse.Plans = [new() { Name = "  ", IncidentType = "Type1" }];
        var sut = NewSut(cfg);

        var ex = await sut.Invoking(s => s.StartAsync(CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain("Name is required");
    }

    [Fact]
    public async Task StartAsync_MissingIncidentType_Throws()
    {
        var cfg = new AppConfig();
        cfg.AI.IncidentResponse.Plans = [new() { Name = "Plan1", IncidentType = "" }];
        var sut = NewSut(cfg);

        var ex = await sut.Invoking(s => s.StartAsync(CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain("IncidentType is required");
        ex.Which.Message.Should().Contain("Plan1");
    }
}
