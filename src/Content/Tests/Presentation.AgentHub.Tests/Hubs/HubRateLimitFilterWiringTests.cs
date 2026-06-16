using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;
using Presentation.AgentHub;
using Presentation.AgentHub.Hubs;
using Xunit;

namespace Presentation.AgentHub.Tests.Hubs;

/// <summary>
/// Regression tests for the SignalR hub-method rate-limit wiring in
/// <see cref="DependencyInjection.AddAgentHubServices"/>. The prior defect registered a
/// "HubSendMessage" token-bucket policy that was never applied to any hub-method invocation
/// (ASP.NET Core's HTTP rate-limiting middleware cannot partition SignalR invocations). These
/// tests pin that <see cref="HubRateLimitFilter"/> is both registered in DI and added to the
/// global SignalR filter chain so the expensive agent-turn methods are actually throttled.
/// </summary>
public sealed class HubRateLimitFilterWiringTests
{
    [Fact]
    public void AddAgentHubServices_RegistersHubRateLimitFilterAsSingleton()
    {
        using var provider = BuildProvider();

        var filter = provider.GetService<HubRateLimitFilter>();

        filter.Should().NotBeNull("the rate-limit filter must be resolvable so SignalR can apply it");
    }

    [Fact]
    public void AddAgentHubServices_AddsHubRateLimitFilterToSignalRFilterChain()
    {
        using var provider = BuildProvider();

        var hubOptions = provider.GetRequiredService<IOptions<HubOptions>>().Value;

        // HubOptions.HubFilters is an internal IList<object>; AddFilter<T>() wraps typeof(T) in an
        // internal HubFilterFactory rather than storing the Type directly. Reflect each entry's
        // Type-typed field to recover the wrapped filter type, so this fails if the rate-limit
        // filter is ever dropped from the SignalR filter chain again.
        var hubFilters = (typeof(HubOptions)
            .GetProperty("HubFilters", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(hubOptions) as IEnumerable<object>)?.ToArray();

        hubFilters.Should().NotBeNull(
            "AddSignalR must register at least the knowledge-scope and rate-limit hub filters");

        static IEnumerable<Type?> WrappedFilterTypes(object filter) =>
            filter is Type t
                ? [t]
                : filter.GetType()
                    .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(f => f.FieldType == typeof(Type))
                    .Select(f => f.GetValue(filter) as Type);

        hubFilters!.SelectMany(WrappedFilterTypes).Should().Contain(typeof(HubRateLimitFilter),
            "the hub-method rate limiter is only effective if it is in the SignalR filter chain");
    }

    private static ServiceProvider BuildProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Dev-auth branch avoids the Azure Identity (Microsoft.Identity.Web) wiring,
                // keeping this a focused DI/wiring test.
                ["Auth:Disabled"] = "true",
            })
            .Build();

        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns(Environments.Development);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentHubServices(configuration, environment.Object);

        return services.BuildServiceProvider();
    }
}
