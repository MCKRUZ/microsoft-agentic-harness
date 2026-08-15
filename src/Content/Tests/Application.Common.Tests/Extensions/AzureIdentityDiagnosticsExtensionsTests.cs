using System.Reflection;
using Application.Common.Extensions;
using Application.Common.Logging;
using FluentAssertions;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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

        rules.Should().Contain(r => r.CategoryName == "Azure" && r.LogLevel == LogLevel.Warning);
    }

    [Fact]
    public void AddAzureIdentityDiagnostics_CarvesAzureIdentityCategoryBackToInformation()
    {
        var services = new ServiceCollection();

        services.AddAzureIdentityDiagnostics();

        using var provider = services.BuildServiceProvider();
        var rules = provider.GetRequiredService<IOptions<LoggerFilterOptions>>().Value.Rules;

        rules.Should().Contain(r => r.CategoryName == "Azure.Identity" && r.LogLevel == LogLevel.Information);
    }

    [Fact]
    public void AddAzureIdentityDiagnostics_CalledAfterAnEarlierAzureCategoryRule_OverridesIt()
    {
        // The generic host (WebApplication.CreateBuilder) binds "Logging:LogLevel" from
        // appsettings.json into LoggerFilterOptions.Rules before this app's own DI composition
        // runs. AddAzureIdentityDiagnostics() is called later, in ConfigureLogging — this test
        // proves what that ordering means: an operator's own "Logging:LogLevel:Azure" override is
        // NOT honored, because the code-registered Warning rule always wins the equal-specificity
        // tie-break (.NET's LoggerRuleSelector picks the last-registered rule when two rules match
        // a category with equal specificity). Simulated here with a hand-built rule instead of real
        // configuration binding, since the outcome depends only on registration order, not on where
        // the earlier rule came from. This is the opposite of what this class's XML doc remarks used
        // to claim, and is documented there now.
        var services = new ServiceCollection();
        // A provider must be registered for IsEnabled() to evaluate rules at all — with none, every
        // category reports disabled regardless of filter level, which would make this test vacuous.
        services.AddLogging(builder => builder.AddConsole());
        services.Configure<LoggerFilterOptions>(o =>
            o.Rules.Add(new LoggerFilterRule(providerName: null, categoryName: "Azure", logLevel: LogLevel.Debug, filter: null)));

        services.AddAzureIdentityDiagnostics();

        using var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Azure");

        logger.IsEnabled(LogLevel.Debug).Should().BeFalse(
            "AddAzureIdentityDiagnostics's Warning filter is registered after the earlier rule " +
            "and wins the equal-specificity tie-break");
        logger.IsEnabled(LogLevel.Warning).Should().BeTrue();
    }

    [Fact]
    public async Task StartAsync_CallsLogForwarderStart()
    {
        // AzureEventSourceLogForwarder exposes no public way to observe whether Start() ran, so this
        // reads its private listener field via reflection — the only way to catch a mutation that
        // silently drops the Start() call (this repo's "registered but never invoked" defect class,
        // see reference_security_control_has_a_caller.md), which every other assertion here would miss.
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
