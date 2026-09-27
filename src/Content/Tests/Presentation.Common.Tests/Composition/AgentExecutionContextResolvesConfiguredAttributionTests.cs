using System.Reflection;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Services.Telemetry;
using Domain.Common.Config;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Presentation.Common.Extensions;
using Xunit;

namespace Presentation.Common.Tests.Composition;

/// <summary>
/// Guards that the real composition root always resolves <c>IAgentExecutionContext</c> through the
/// constructor that takes an <c>IAgentTelemetryAttribution</c>, never the parameterless one.
/// </summary>
/// <remarks>
/// <para>
/// <c>AgentExecutionContext</c> has two public constructors precisely so direct construction outside DI
/// (chiefly tests) doesn't have to carry attribution wiring it doesn't care about — see its own remarks
/// (#737). That convenience has one failure mode: if BOTH production registrations for
/// <c>IAgentTelemetryAttribution</c> were ever deleted (Application.AI.Common's default and
/// Infrastructure.Observability's Agent 365 override — either alone still leaves one in place), .NET's
/// own constructor-selection rule falls back to the parameterless constructor silently: no exception,
/// and <c>ValidateOnBuildSweepTests</c> does not catch it, because both constructors are perfectly valid
/// to build. Confirmed empirically, not assumed: with both registrations removed,
/// <c>GetRequiredService&lt;IAgentExecutionContext&gt;()</c> still succeeds. The symptom is exactly the
/// no-symptom failure #737 exists to eliminate — a host's activity simply stops reaching the tenant's
/// Agent 365 records, with nothing anywhere to say so. Security review on #737 named this gap.
/// </para>
/// <para>
/// The assertion can't be "resolves the same instance <c>GetRequiredService&lt;IAgentTelemetryAttribution&gt;()</c>
/// returns" — that call itself throws when nothing is registered, which is the exact scenario this test
/// exists to catch. Instead it checks the wired attribution is never the parameterless constructor's own
/// static sentinel (<see cref="NoOpAgentTelemetryAttribution.Instance"/>) — the one object only that
/// constructor ever supplies. Reflection is the only way to observe which constructor ran.
/// </para>
/// </remarks>
public sealed class AgentExecutionContextResolvesConfiguredAttributionTests
{
    [Fact]
    public void ResolvedExecutionContext_NeverUsesTheParameterlessConstructorsSentinel()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.RegisterConfigSections(configuration);
        var appConfig = configuration.GetSection("AppConfig").Get<AppConfig>() ?? new AppConfig();
        services.BuildGlobalSolutionServices(appConfig, includeHealthChecksUI: false);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        using var scope = provider.CreateScope();
        var executionContext = scope.ServiceProvider.GetRequiredService<IAgentExecutionContext>();

        var wiredAttribution = typeof(Application.AI.Common.Services.Agent.AgentExecutionContext)
            .GetField("_attribution", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(executionContext);

        Assert.NotSame(NoOpAgentTelemetryAttribution.Instance, wiredAttribution);
    }
}
