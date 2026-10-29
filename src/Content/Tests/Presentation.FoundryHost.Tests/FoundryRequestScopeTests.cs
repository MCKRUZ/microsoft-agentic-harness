using System.Reflection;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Services;
using Application.AI.Common.Services.Governance;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Presentation.FoundryHost.Tests;

/// <summary>
/// Tests for <see cref="FoundryRequestScope"/>, the per-request wrapper this host's single, process-lived
/// agent runs inside.
/// </summary>
public sealed class FoundryRequestScopeTests
{
    private static ServiceProvider ProviderWithAdmissionPipeline()
    {
        var services = new ServiceCollection();
        services.AddScoped<IToolCallAdmissionPipeline>(
            _ => DispatchProxy.Create<IToolCallAdmissionPipeline, UnusedPipeline>());
        // These requests carry no platform user id, so the writer is never asked to set a scope.
        services.AddScoped<IKnowledgeScopeWriter>(_ => DispatchProxy.Create<IKnowledgeScopeWriter, UnusedPipeline>());
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// #541: two concurrent requests whose first tool call reuses a provider-issued id must each be able to
    /// record it. With one claim set per middleware instance, the second request lost its tool result from
    /// its trace.
    /// </summary>
    [Fact]
    public async Task RunAsync_ConcurrentRequestsReusingACallId_EachClaimItInItsOwnSet()
    {
        await using var provider = ProviderWithAdmissionPipeline();
        var bothInside = new Barrier(2);
        var seen = new ReplayedToolCallSet?[2];
        var claimed = new bool[2];

        Task Request(int i) => Task.Run(() => FoundryRequestScope.RunAsync(provider, new DefaultHttpContext(), () =>
        {
            seen[i] = ReplayedToolCallScope.Current;
            claimed[i] = ReplayedToolCallScope.Current!.TryClaim("call_1");

            // Both requests are inside their scopes at the same time, so neither result depends on order.
            bothInside.SignalAndWait(TimeSpan.FromSeconds(10)).Should().BeTrue();
            return Task.CompletedTask;
        }));

        await Task.WhenAll(Request(0), Request(1));

        claimed.Should().AllBeEquivalentTo(true, "each request records its own tool result");
        seen[0].Should().NotBeNull();
        seen[0].Should().NotBeSameAs(seen[1], "concurrent requests must not share a claim set");
    }

    [Fact]
    public async Task RunAsync_ArmsTheAdmissionPipelineForTheRequest()
    {
        // #478's guarantee, which moved into this type: without it GovernedAIFunction skips admission.
        await using var provider = ProviderWithAdmissionPipeline();
        IToolCallAdmissionPipeline? armed = null;

        await FoundryRequestScope.RunAsync(provider, new DefaultHttpContext(), () =>
        {
            armed = ToolAdmissionAccessor.Current;
            return Task.CompletedTask;
        });

        armed.Should().NotBeNull();
    }

    [Fact]
    public async Task RunAsync_RestoresTheCallersClaimSetAfterwards()
    {
        await using var provider = ProviderWithAdmissionPipeline();
        var outer = new ReplayedToolCallSet();
        ReplayedToolCallScope.Current = outer;
        try
        {
            await FoundryRequestScope.RunAsync(provider, new DefaultHttpContext(), () => Task.CompletedTask);

            ReplayedToolCallScope.Current.Should().BeSameAs(outer);
        }
        finally
        {
            ReplayedToolCallScope.Current = null;
        }
    }

    /// <summary>A service nothing in these tests calls; it only has to exist to be resolved.</summary>
    public class UnusedPipeline : DispatchProxy
    {
        /// <inheritdoc />
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new NotSupportedException($"{targetMethod?.Name} is not used by these tests.");
    }
}
