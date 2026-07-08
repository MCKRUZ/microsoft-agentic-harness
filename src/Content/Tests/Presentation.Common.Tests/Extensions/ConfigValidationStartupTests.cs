using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Presentation.Common.Extensions;
using Presentation.Common.Startup;
using Xunit;

namespace Presentation.Common.Tests.Extensions;

/// <summary>
/// Proves that every AppConfig section with a FluentValidation config validator fails fast at
/// startup when the bound values are invalid, and that default (omitted) sections keep passing
/// so existing hosts boot unchanged.
/// </summary>
/// <remarks>
/// Regression guard for the Wave-2 audit finding "config validators are inert machinery":
/// the validators existed and were unit-tested, but nothing wired them into the options
/// pipeline, so invalid appsettings values started up silently. These tests exercise the
/// wiring itself — <c>RegisterConfigSections</c> → <see cref="IStartupValidator"/> — not the
/// individual validator rules (those have their own unit tests).
/// </remarks>
public class ConfigValidationStartupTests
{
    private static ServiceProvider BuildProvider(Dictionary<string, string?> settings)
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        services.RegisterConfigSections(config);
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// One obviously-invalid probe per validated config section. Each row violates an
    /// unconditional (or trivially armed) rule of the section's validator.
    /// </summary>
    public static TheoryData<string, Dictionary<string, string?>> InvalidSectionValues => new()
    {
        {
            "ResilienceConfig",
            new Dictionary<string, string?> { ["AppConfig:AI:Resilience:Retry:BackoffType"] = "Bogus" }
        },
        {
            "DriftDetectionConfig",
            new Dictionary<string, string?> { ["AppConfig:AI:DriftDetection:EwmaLambda"] = "0" }
        },
        {
            "LearningsConfig",
            new Dictionary<string, string?> { ["AppConfig:AI:Learnings:FeedbackAlpha"] = "0" }
        },
        {
            "EscalationConfig",
            new Dictionary<string, string?> { ["AppConfig:AI:Governance:Escalation:DefaultTimeoutAction"] = "NotAnAction" }
        },
        {
            "WorkMemoryConfig",
            new Dictionary<string, string?> { ["AppConfig:AI:WorkMemory:StoreProvider"] = "not_a_provider" }
        },
        {
            "HarmonicMemoryConfig",
            new Dictionary<string, string?> { ["AppConfig:AI:HarmonicMemory:BatchAtSessionFlush"] = "true" }
        },
        {
            "LearningsRecallConfig",
            new Dictionary<string, string?> { ["AppConfig:AI:LearningsRecall:MaxResults"] = "0" }
        },
        {
            "GitOpsConfig",
            new Dictionary<string, string?>
            {
                ["AppConfig:AI:GitOps:Enabled"] = "true",
                ["AppConfig:AI:GitOps:ActiveController"] = "not-a-controller",
            }
        },
        {
            "IacConfig",
            new Dictionary<string, string?>
            {
                ["AppConfig:AI:Iac:Enabled"] = "true",
                ["AppConfig:AI:Iac:BlockingSeverity"] = "NotASeverity",
            }
        },
        {
            "DataClassificationConfig",
            new Dictionary<string, string?>
            {
                ["AppConfig:AI:Governance:DataClassification:Mode"] = "Audit",
                ["AppConfig:AI:Governance:DataClassification:ResultCacheTtl"] = "-00:05:00",
            }
        },
        {
            "ContentCaptureConfig",
            new Dictionary<string, string?>
            {
                ["AppConfig:AI:Telemetry:ContentCapture:Enabled"] = "true",
                ["AppConfig:AI:Telemetry:ContentCapture:RedactionCategories:0"] = "NotACategory",
            }
        },
    };

    [Theory]
    [MemberData(nameof(InvalidSectionValues))]
    public void RegisterConfigSections_InvalidSectionValue_FailsStartupValidation(
        string sectionLabel, Dictionary<string, string?> invalidSettings)
    {
        using var provider = BuildProvider(invalidSettings);

        // ValidateOnStart registers the IStartupValidator that hosts run at boot.
        // If this resolves to null, config validation is not wired at startup at all
        // (the original audit finding).
        var startupValidator = provider.GetService<IStartupValidator>();
        startupValidator.Should().NotBeNull(
            $"config validation for {sectionLabel} must be wired into startup via ValidateOnStart");

        var act = () => startupValidator!.Validate();
        act.Should().Throw<OptionsValidationException>(
            $"an invalid {sectionLabel} value must fail host startup instead of passing silently");
    }

    [Fact]
    public void RegisterConfigSections_EmptyConfiguration_PassesStartupValidation()
    {
        // Hosts omit most validated sections entirely (FoundryHost, MCPServer, …) — the
        // config-class defaults must satisfy every validator or wiring validation would
        // break currently-valid hosts.
        using var provider = BuildProvider([]);

        var startupValidator = provider.GetService<IStartupValidator>();
        startupValidator.Should().NotBeNull(
            "config validation must be wired into startup via ValidateOnStart");

        var act = () => startupValidator!.Validate();
        act.Should().NotThrow(
            "default config values must pass validation so hosts without these sections keep booting");
    }

    [Fact]
    public void RegisterConfigSections_CurrentHostSectionShape_PassesStartupValidation()
    {
        // Mirrors the values every host appsettings.json actually ships today
        // (AgentHub/ConsoleUI/EvalRunner/FoundryHost) — wiring validation must not
        // reject the existing, valid configuration.
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["AppConfig:AI:Resilience:Enabled"] = "false",
            ["AppConfig:AI:Resilience:FallbackChain:0:ClientType"] = "AzureOpenAI",
            ["AppConfig:AI:Resilience:FallbackChain:0:DeploymentId"] = "gpt-4o",
            ["AppConfig:AI:DriftDetection:Enabled"] = "true",
            ["AppConfig:AI:DriftDetection:EwmaLambda"] = "0.2",
            ["AppConfig:AI:Learnings:Enabled"] = "true",
            ["AppConfig:AI:Learnings:FeedbackAlpha"] = "0.25",
            ["AppConfig:AI:Governance:Escalation:Enabled"] = "true",
            ["AppConfig:AI:Governance:Escalation:DefaultTimeoutAction"] = "DenyAndEscalate",
            ["AppConfig:AI:Governance:Escalation:PriorityLevels:Blocking:TimeoutSeconds"] = "300",
            ["AppConfig:AI:Governance:DataClassification:Mode"] = "Off",
        });

        var startupValidator = provider.GetService<IStartupValidator>();
        startupValidator.Should().NotBeNull();

        var act = () => startupValidator!.Validate();
        act.Should().NotThrow("the configuration every host ships today is valid and must keep booting");
    }

    [Fact]
    public async Task Host_WithInvalidConfigValue_FailsFastOnStart()
    {
        // Representative end-to-end proof at the real host level: a web-style IHost runs
        // IStartupValidator inside StartAsync, so an invalid value aborts boot.
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AppConfig:AI:DriftDetection:EwmaLambda"] = "0",
        });
        builder.Services.RegisterConfigSections(builder.Configuration);

        using var host = builder.Build();

        var act = () => host.StartAsync();
        await act.Should().ThrowAsync<OptionsValidationException>(
            "an IHost-based composition root must refuse to start on invalid config");
    }

    [Fact]
    public async Task StartupRegistrationSmokeCheck_WithInvalidConfigValue_ThrowsOptionsValidation()
    {
        // Console-style hosts (ConsoleUI, EvalRunner, FoundryHost) compose a bare
        // ServiceCollection and start hosted services manually — no IHost means the
        // native IStartupValidator hook never runs. StartupRegistrationSmokeCheck is
        // the hosted service every host runs, so it must trigger startup options
        // validation for those hosts.
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AppConfig:AI:Learnings:FeedbackAlpha"] = "0",
            })
            .Build();
        services.RegisterConfigSections(config);
        await using var provider = services.BuildServiceProvider();

        var smokeCheck = new StartupRegistrationSmokeCheck(
            provider, NullLogger<StartupRegistrationSmokeCheck>.Instance);

        var act = () => smokeCheck.StartAsync(CancellationToken.None);
        await act.Should().ThrowAsync<OptionsValidationException>(
            "console hosts rely on the smoke check to run startup options validation");
    }
}
