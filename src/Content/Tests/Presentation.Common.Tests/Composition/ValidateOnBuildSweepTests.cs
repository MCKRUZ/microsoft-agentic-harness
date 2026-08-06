using Domain.Common.Config;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Presentation.Common.Extensions;
using Xunit;

namespace Presentation.Common.Tests.Composition;

/// <summary>
/// Self-maintaining guard for audit item H2: the production composition root must
/// pass <c>ValidateOnBuild = true</c>, meaning EVERY registered service — including
/// every MediatR handler discovered by assembly scanning — can actually be constructed.
/// </summary>
/// <remarks>
/// <para>
/// Before this guard, four globally-scanned handlers depended on interfaces supplied by
/// only one host (AgentHub's SignalR notifiers) or one opt-in subsystem (the eval runner,
/// the prompt-usage store). Any other host registered the handler but could not build it —
/// a latent runtime crash the moment that command was dispatched. <c>ValidateOnBuild</c>
/// converts that silent-until-dispatched failure into a loud boot failure, and this test
/// converts it further into a caught-at-CI failure.
/// </para>
/// <para>
/// The fix is a No-op / not-configured default for each such dependency (mirroring the
/// existing <c>NullEvalRunNotifier</c> pattern), so the graph is constructible in every
/// host; the real host-specific implementation still wins via last-registration-wins.
/// If a future change adds a handler whose dependency has no default, this test fails with
/// the exact unresolved service — not a customer's production stack trace.
/// </para>
/// <para>
/// #251's original ~106 failures came nearly all from conditionally-registered handlers
/// whose dependencies are only present under some configurations (e.g., handlers gated by
/// Governance.Enabled). Testing only the all-features-off baseline is blind to that class.
/// This test is parameterized to cover both the baseline and representative features-on
/// configurations, closing the gap that #251's first fix left.
/// </para>
/// </remarks>
public sealed class ValidateOnBuildSweepTests
{
    /// <summary>
    /// All-features-off baseline: the default, all-features-off registration set every host
    /// shares before its host-specific overrides. This is the baseline that must always
    /// be constructible.
    /// </summary>
    [Fact]
    public void ProductionCompositionRoot_AllFeaturesOff_BuildsWithValidateOnBuild()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        BuildAndValidate(configuration);
    }

    /// <summary>
    /// Governance-enabled configuration: handlers that depend on governance services are only
    /// registered when <c>AppConfig:AI:Governance:Enabled = true</c>. Validates that the graph
    /// is constructible when this feature is on.
    /// </summary>
    [Fact]
    public void ProductionCompositionRoot_GovernanceEnabled_BuildsWithValidateOnBuild()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "AppConfig:AI:Governance:Enabled", "true" }
            })
            .Build();

        BuildAndValidate(configuration);
    }

    private static void BuildAndValidate(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.RegisterConfigSections(configuration);
        var appConfig = configuration.GetSection("AppConfig").Get<AppConfig>() ?? new AppConfig();
        services.BuildGlobalSolutionServices(appConfig, includeHealthChecksUI: false);

        // ValidateOnBuild eagerly constructs every non-open-generic descriptor and throws an
        // AggregateException listing ALL that cannot be built. ValidateScopes is kept on to
        // match the production hosts (captive-dependency guard, audit item H2's sibling).
        var exception = Record.Exception(() =>
        {
            using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });
        });

        Assert.Null(exception);
    }
}
