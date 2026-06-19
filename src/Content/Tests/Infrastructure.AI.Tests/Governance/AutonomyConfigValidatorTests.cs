using Domain.Common.Config;
using Domain.Common.Config.AI.Permissions;
using FluentAssertions;
using Infrastructure.AI.Governance;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Infrastructure.AI.Tests.Governance;

/// <summary>
/// Boot-time validator tests for <see cref="AutonomyConfigValidator"/> (PR-4).
/// </summary>
public sealed class AutonomyConfigValidatorTests
{
    private sealed class StubEnv : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "TestApp";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private static AutonomyConfigValidator Build(
        AppConfig config,
        string environmentName = "Development")
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHostEnvironment>(new StubEnv { EnvironmentName = environmentName });
        var provider = services.BuildServiceProvider();

        var monitor = new StaticOptionsMonitor<AppConfig>(config);

        return new AutonomyConfigValidator(
            provider,
            monitor,
            NullLogger<AutonomyConfigValidator>.Instance);
    }

    private static AppConfig GradedEnabled(Action<GradedAutonomyConfig> configure)
    {
        var cfg = new AppConfig();
        cfg.AI.Permissions.GradedAutonomy.Enabled = true;
        configure(cfg.AI.Permissions.GradedAutonomy);
        return cfg;
    }

    [Fact]
    public async Task StartAsync_GradedDisabled_NoOp()
    {
        var cfg = new AppConfig(); // GradedAutonomy.Enabled defaults to false
        var sut = Build(cfg);

        var act = async () => await sut.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StartAsync_GradedEnabled_NoRules_NoOp()
    {
        var cfg = GradedEnabled(_ => { });
        var sut = Build(cfg);

        var act = async () => await sut.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StartAsync_CriticalAutoApproveAnywhere_Throws()
    {
        var cfg = GradedEnabled(g =>
        {
            g.PerEnvironment["Development"] = new EnvironmentAutonomyConfig
            {
                PerBlastRadius =
                {
                    ["Critical"] = new BlastRadiusRuleConfig { Decision = "AutoApprove" }
                }
            };
        });

        var sut = Build(cfg);

        var act = async () => await sut.StartAsync(CancellationToken.None);

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain("Critical");
        ex.Which.Message.Should().Contain("AutoApprove");
    }

    [Fact]
    public async Task StartAsync_ProductionHighAutoApprove_Throws()
    {
        var cfg = GradedEnabled(g =>
        {
            g.PerEnvironment["Production"] = new EnvironmentAutonomyConfig
            {
                PerBlastRadius =
                {
                    ["High"] = new BlastRadiusRuleConfig { Decision = "AutoApprove" }
                }
            };
        });

        var sut = Build(cfg);

        var act = async () => await sut.StartAsync(CancellationToken.None);

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain("Production");
        ex.Which.Message.Should().Contain("High");
    }

    [Fact]
    public async Task StartAsync_InvalidEnumNames_Throws()
    {
        var cfg = GradedEnabled(g =>
        {
            g.PerEnvironment["Development"] = new EnvironmentAutonomyConfig
            {
                PerBlastRadius =
                {
                    ["Catastrophic"] = new BlastRadiusRuleConfig { Decision = "Maybe" }
                }
            };
        });

        var sut = Build(cfg);

        var act = async () => await sut.StartAsync(CancellationToken.None);

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain("Catastrophic");
    }

    [Fact]
    public async Task StartAsync_PerSkillTierLooserThanBaseline_Throws()
    {
        var cfg = GradedEnabled(g =>
        {
            g.PerSkill["bossy"] = new SkillAutonomyConfig
            {
                Tier = "Autonomous"
            };
        });
        cfg.AI.Permissions.DefaultAutonomyLevel = "Restricted";

        var sut = Build(cfg);

        var act = async () => await sut.StartAsync(CancellationToken.None);

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain("bossy");
        ex.Which.Message.Should().Contain("looser");
    }

    [Fact]
    public async Task StartAsync_InvalidDefaultAutonomyLevel_Throws()
    {
        // The tool-risk gate parses DefaultAutonomyLevel at runtime; a typo must fail boot
        // (when graded autonomy is enabled) rather than silently disable the gate.
        var cfg = GradedEnabled(_ => { });
        cfg.AI.Permissions.DefaultAutonomyLevel = "NotATier";

        var sut = Build(cfg);

        var act = async () => await sut.StartAsync(CancellationToken.None);

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain("DefaultAutonomyLevel");
    }

    [Fact]
    public async Task StartAsync_PerSkillCriticalAutoApprove_Throws()
    {
        var cfg = GradedEnabled(g =>
        {
            g.PerSkill["dangerous"] = new SkillAutonomyConfig
            {
                PerBlastRadius =
                {
                    ["Critical"] = new BlastRadiusRuleConfig { Decision = "AutoApprove" }
                }
            };
        });

        var sut = Build(cfg);

        var act = async () => await sut.StartAsync(CancellationToken.None);

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain("dangerous");
    }

    [Fact]
    public async Task StartAsync_BlankStateChangerOptIn_Throws()
    {
        var cfg = GradedEnabled(g =>
        {
            g.StateChangerOptIns.Add("   ");
        });

        var sut = Build(cfg);

        var act = async () => await sut.StartAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task StartAsync_ValidConfig_NoThrow()
    {
        var cfg = GradedEnabled(g =>
        {
            g.PerEnvironment["Development"] = new EnvironmentAutonomyConfig
            {
                PerBlastRadius =
                {
                    ["Trivial"] = new BlastRadiusRuleConfig { Decision = "AutoApprove" },
                    ["Low"] = new BlastRadiusRuleConfig { Decision = "AutoApprove" }
                }
            };
            g.PerEnvironment["Production"] = new EnvironmentAutonomyConfig
            {
                PerBlastRadius =
                {
                    ["Trivial"] = new BlastRadiusRuleConfig { Decision = "AutoApprove" }
                }
            };
            g.StateChangerOptIns.Add("trusted-skill");
        });

        var sut = Build(cfg);

        var act = async () => await sut.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StaticOptionsMonitor(T value) { CurrentValue = value; }
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
