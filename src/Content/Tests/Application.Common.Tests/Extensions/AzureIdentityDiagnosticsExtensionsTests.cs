using System.Reflection;
using Application.Common;
using Application.Common.Extensions;
using Application.Common.Logging;
using FluentAssertions;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenTelemetry.Logs;
using Xunit;

namespace Application.Common.Tests.Extensions;

public class AzureIdentityDiagnosticsExtensionsTests
{
    [Fact]
    public void AddAzureIdentityDiagnostics_RegistersLogForwarderAsResolvableSingleton()
    {
        var services = new ServiceCollection();

        services.AddAzureIdentityDiagnostics();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<AzureEventSourceLogForwarder>().Should().NotBeNull();
    }

    [Fact]
    public void AddAzureIdentityDiagnostics_RegistersHostedService()
    {
        var services = new ServiceCollection();

        services.AddAzureIdentityDiagnostics();

        using var provider = services.BuildServiceProvider();
        var hostedServices = provider.GetServices<IHostedService>();

        hostedServices.Should().ContainSingle(s => s is AzureIdentityLogForwarderHostedService);
    }

    [Fact]
    public void AddAzureIdentityDiagnostics_FiltersAzureCategoryToWarningByDefault()
    {
        var services = new ServiceCollection();

        services.AddAzureIdentityDiagnostics();

        using var provider = services.BuildServiceProvider();
        var rules = provider.GetRequiredService<IOptions<LoggerFilterOptions>>().Value.Rules;

        rules.Should().Contain(r => r.ProviderName == null && r.CategoryName == "Azure" && r.LogLevel == LogLevel.Warning);
    }

    [Fact]
    public void AddAzureIdentityDiagnostics_CarvesAzureIdentityCategoryBackToInformation()
    {
        var services = new ServiceCollection();

        services.AddAzureIdentityDiagnostics();

        using var provider = services.BuildServiceProvider();
        var rules = provider.GetRequiredService<IOptions<LoggerFilterOptions>>().Value.Rules;

        rules.Should().Contain(r => r.ProviderName == null && r.CategoryName == "Azure.Identity" && r.LogLevel == LogLevel.Information);
    }

    [Fact]
    public void AddAzureIdentityDiagnostics_CalledAfterAnEarlierAzureCategoryRule_DoesNotOverrideIt()
    {
        // A naive `builder.AddFilter("Azure", Warning)` inside AddLogging would win an operator's
        // own "Logging:LogLevel:Azure" configuration outright — .NET's LoggerRuleSelector picks the
        // last-registered rule when two rules match a category with equal specificity, and
        // AddAzureIdentityDiagnostics() runs after a host's own config-bound logging setup. This
        // proves the actual implementation avoids that trap: PostConfigure<LoggerFilterOptions> only
        // adds the "Azure" default when no global (provider-unscoped) rule already targets that
        // exact category, so a pre-existing rule — from configuration or earlier code — is left in
        // place. Simulated here with a hand-built rule instead of real configuration binding, since
        // the outcome depends only on whether a rule for the category already exists, not on where
        // it came from.
        var services = new ServiceCollection();
        // A provider must be registered for IsEnabled() to evaluate rules at all — with none, every
        // category reports disabled regardless of filter level, which would make this test vacuous.
        services.AddLogging(builder => builder.AddConsole());
        services.Configure<LoggerFilterOptions>(o =>
            o.Rules.Add(new LoggerFilterRule(providerName: null, categoryName: "Azure", logLevel: LogLevel.Debug, filter: null)));

        services.AddAzureIdentityDiagnostics();

        using var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Azure");

        logger.IsEnabled(LogLevel.Debug).Should().BeTrue(
            "the operator's own pre-existing 'Azure' rule must be preserved, not silently replaced " +
            "by AddAzureIdentityDiagnostics's Warning default");
    }

    [Fact]
    public void AddAzureIdentityDiagnostics_NoPriorAzureCategoryRule_AppliesTheWarningDefault()
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole());

        services.AddAzureIdentityDiagnostics();

        using var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Azure");

        logger.IsEnabled(LogLevel.Information).Should().BeFalse(
            "with no pre-existing 'Azure' rule, the Warning default should apply");
        logger.IsEnabled(LogLevel.Warning).Should().BeTrue();
    }

    [Fact]
    public void AddAzureIdentityDiagnostics_RegistersProviderScopedRulesForOpenTelemetryExport()
    {
        // .NET's LoggerRuleSelector treats ANY provider-scoped rule as strictly better than a
        // category-only rule when selecting for that provider — regardless of category specificity
        // or registration order. OpenTelemetry's own AddFilter<OpenTelemetryLoggerProvider>(category:
        // null, MinExportLevel) is exactly such a rule, so the global "Azure"/"Azure.Identity" rules
        // above would never apply to the OTel export sink at all, letting Azure SDK diagnostics
        // (which can carry resource identifiers) reach exported telemetry unfiltered. This proves the
        // provider-scoped rules that close that gap actually exist.
        var services = new ServiceCollection();

        services.AddAzureIdentityDiagnostics();

        using var provider = services.BuildServiceProvider();
        var rules = provider.GetRequiredService<IOptions<LoggerFilterOptions>>().Value.Rules;
        var otelProviderName = typeof(OpenTelemetryLoggerProvider).FullName;

        rules.Should().Contain(r =>
            r.ProviderName == otelProviderName && r.CategoryName == "Azure" && r.LogLevel == LogLevel.Warning);
        rules.Should().Contain(r =>
            r.ProviderName == otelProviderName && r.CategoryName == "Azure.Identity" && r.LogLevel == LogLevel.Information);
    }

    [Fact]
    public void AddAzureIdentityDiagnostics_CalledAfterAnEarlierOpenTelemetryScopedRule_DoesNotOverrideIt()
    {
        // The OpenTelemetry-scoped defaults must respect an explicit prior rule for that exact
        // (provider, category) pair too, not just the global rules — otherwise an operator who
        // deliberately configures a STRICTER OTel export level for "Azure" (e.g. Error-only, to keep
        // resource identifiers out of exported telemetry) would have this method silently loosen it
        // back to Warning, which is the mirror-image of the bypass this method exists to close.
        var services = new ServiceCollection();
        var otelProviderName = typeof(OpenTelemetryLoggerProvider).FullName;
        services.Configure<LoggerFilterOptions>(o =>
            o.Rules.Add(new LoggerFilterRule(otelProviderName, "Azure", LogLevel.Error, filter: null)));

        services.AddAzureIdentityDiagnostics();

        using var provider = services.BuildServiceProvider();
        var rules = provider.GetRequiredService<IOptions<LoggerFilterOptions>>().Value.Rules;

        rules.Should().Contain(r => r.ProviderName == otelProviderName && r.CategoryName == "Azure" && r.LogLevel == LogLevel.Error);
        rules.Should().NotContain(r => r.ProviderName == otelProviderName && r.CategoryName == "Azure" && r.LogLevel == LogLevel.Warning);
    }

    [Fact]
    public void AddAzureIdentityDiagnostics_OperatorSetsStricterCategoryNullExportLevel_ClampsToIt()
    {
        // The realistic way an operator restricts OTel export verbosity is a category-null
        // MinExportLevel rule (exactly what OpenTelemetryServiceCollectionExtensions.AddLogsSignal
        // registers), not a category-specific one — and a category-specific rule always wins
        // provider selection over a category-null one regardless of which is stricter. A hardcoded
        // Warning/Information default would therefore silently let "Azure"/"Azure.Identity" export
        // MORE verbosely than an operator's stricter category-null cap (e.g. Error-only, configured
        // specifically to keep resource identifiers out of exported telemetry) — caught by this
        // repo's correctness-review CI gate on the first version of this fix. The OTel-scoped level
        // must clamp to the stricter of the two.
        var services = new ServiceCollection();
        var otelProviderName = typeof(OpenTelemetryLoggerProvider).FullName;
        services.Configure<LoggerFilterOptions>(o =>
            o.Rules.Add(new LoggerFilterRule(otelProviderName, categoryName: null, LogLevel.Error, filter: null)));

        services.AddAzureIdentityDiagnostics();

        using var provider = services.BuildServiceProvider();
        var rules = provider.GetRequiredService<IOptions<LoggerFilterOptions>>().Value.Rules;

        rules.Should().Contain(r => r.ProviderName == otelProviderName && r.CategoryName == "Azure" && r.LogLevel == LogLevel.Error,
            "the operator's stricter Error-only export cap must win over this method's Warning default");
        rules.Should().Contain(r => r.ProviderName == otelProviderName && r.CategoryName == "Azure.Identity" && r.LogLevel == LogLevel.Error,
            "the operator's stricter Error-only export cap must win over this method's Information default");
    }

    [Fact]
    public void AddAzureIdentityDiagnostics_OperatorSetsMorePermissiveCategoryNullExportLevel_KeepsTheSaferDefault()
    {
        // The mirror case of the clamp above: an operator's category-null export level that is MORE
        // permissive than this method's defaults (e.g. Trace, to capture everything for a debugging
        // session) must not loosen the "Azure"/"Azure.Identity" export levels below the safe
        // defaults — that's the original bypass this method exists to close.
        var services = new ServiceCollection();
        var otelProviderName = typeof(OpenTelemetryLoggerProvider).FullName;
        services.Configure<LoggerFilterOptions>(o =>
            o.Rules.Add(new LoggerFilterRule(otelProviderName, categoryName: null, LogLevel.Trace, filter: null)));

        services.AddAzureIdentityDiagnostics();

        using var provider = services.BuildServiceProvider();
        var rules = provider.GetRequiredService<IOptions<LoggerFilterOptions>>().Value.Rules;

        rules.Should().Contain(r => r.ProviderName == otelProviderName && r.CategoryName == "Azure" && r.LogLevel == LogLevel.Warning);
        rules.Should().Contain(r => r.ProviderName == otelProviderName && r.CategoryName == "Azure.Identity" && r.LogLevel == LogLevel.Information);
    }

    [Fact]
    public void AddApplicationCommonDependencies_ResolvesLogForwarderAndHostedServiceFromTheRealCompositionRoot()
    {
        // This repo's own CLAUDE.md documents a recurring defect class: a service registered but
        // never invoked from the real composition root, caught only by resolving it there rather than
        // from a bare ServiceCollection built solely for a unit test (issues #247, #331). Every other
        // test in this class calls AddAzureIdentityDiagnostics() directly; this one instead goes
        // through the actual entry point (AddApplicationCommonDependencies -> ConfigureLogging) so a
        // future refactor that drops the wiring call fails a test, not just silently ships dark.
        var services = new ServiceCollection();

        services.AddApplicationCommonDependencies();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<AzureEventSourceLogForwarder>().Should().NotBeNull();
        provider.GetServices<IHostedService>().Should().ContainSingle(s => s is AzureIdentityLogForwarderHostedService);
    }

    [Fact]
    public async Task StartAsync_CallsLogForwarderStart()
    {
        // AzureEventSourceLogForwarder exposes no public way to observe whether Start() ran, so this
        // reads its private listener field via reflection — the only way to catch a mutation that
        // silently drops the Start() call (this repo's "registered but never invoked" defect class,
        // see reference_security_control_has_a_caller.md), which every other assertion here would miss.
        // Fragile by nature: pinned to Microsoft.Extensions.Azure 1.14.0's current internal layout
        // (Directory.Packages.props) — a future package bump that renames or restructures the
        // internal listener field will need this test updated alongside it.
        using var loggerFactory = NullLoggerFactory.Instance;
        var forwarder = new AzureEventSourceLogForwarder(loggerFactory);
        var service = new AzureIdentityLogForwarderHostedService(
            forwarder, NullLogger<AzureIdentityLogForwarderHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);

        var listenerField = typeof(AzureEventSourceLogForwarder).GetField(
            "_listener", BindingFlags.NonPublic | BindingFlags.Instance);
        listenerField.Should().NotBeNull("AzureEventSourceLogForwarder must expose a _listener field to assert against");
        listenerField!.GetValue(forwarder).Should().NotBeNull(
            "StartAsync must call AzureEventSourceLogForwarder.Start(), which sets this field");
    }
}
