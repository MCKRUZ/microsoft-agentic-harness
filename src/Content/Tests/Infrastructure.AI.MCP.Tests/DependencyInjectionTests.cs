using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Tools;
using Domain.Common.Config.AI;
using FluentAssertions;
using Infrastructure.AI.MCP.Resources;
using Infrastructure.AI.MCP.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Infrastructure.AI.MCP.Tests;

/// <summary>
/// Integration tests for <see cref="DependencyInjection.AddMcpClientDependencies"/>
/// verifying correct service registration and resolution via a real DI container.
/// </summary>
public sealed class DependencyInjectionTests : IAsyncLifetime
{
    private ServiceProvider? _provider;

    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();

        // Register prerequisites
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.Configure<AIConfig>(_ => { });
        services.Configure<Domain.Common.Config.MetaHarness.MetaHarnessConfig>(_ => { });
        // McpConnectionManager now has a hard dependency on the SSRF guard; the egress
        // layer normally registers it. Provide it here so resolution succeeds.
        services.AddSingleton(TestSsrf.HandlerFactory());
        // The tool provider is published as a scanning decorator with a hard dependency on the
        // security scanner, which the governance layer normally registers. Same reasoning as the
        // SSRF guard above: a missing security dependency must fail resolution, not be skipped.
        services.AddSingleton(Mock.Of<IMcpSecurityScanner>());
        // Same again for the behaviour registry the recording decorator writes to. A host that never
        // wired it would publish MCP tools with nothing on file about what they do, which the
        // behaviour posture reads as "unknown" for every one of them.
        services.AddSingleton(Mock.Of<IToolBehaviorRegistry>());
        // McpConnectionManager's bundle-egress-attribution chain (#370 security fix) has a hard
        // constructor-time dependency on IAmbientRequestScope and IEgressAuditWriter — normally
        // registered by the egress layer. Same reasoning as the SSRF guard above: an unwired egress layer
        // must fail resolution, not silently produce an unattributed bundle connection.
        services.AddSingleton(Mock.Of<IAmbientRequestScope>());
        services.AddSingleton(Mock.Of<Application.AI.Common.Interfaces.Egress.IEgressAuditWriter>());

        services.AddMcpClientDependencies();

        _provider = services.BuildServiceProvider();
        return _provider;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_provider is not null)
            await _provider.DisposeAsync();
    }

    [Fact]
    public void AddMcpClientDependencies_RegistersMcpConnectionManager()
    {
        var provider = BuildProvider();

        var manager = provider.GetService<McpConnectionManager>();

        manager.Should().NotBeNull();
    }

    [Fact]
    public void AddMcpClientDependencies_PublishesTheDecoratedProviderRatherThanTheBareTransport()
    {
        var provider = BuildProvider();

        var toolProvider = provider.GetService<IMcpToolProvider>();

        toolProvider.Should().NotBeNull();
        // CachingMcpToolProvider (#495) is now outermost — see its own remarks for why a cache hit must
        // skip everything downstream, not just the wire. Pinning the outermost type here is what makes
        // this test fail if that decorator is ever dropped from the chain or registered somewhere other
        // than outermost.
        toolProvider.Should().BeOfType<CachingMcpToolProvider>(
            "every consumer resolves the interface, so the decorators have to be what the interface "
            + "returns — registering the bare transport provider would leave tool definitions from "
            + "external servers unscanned and their declared behaviour unrecorded, and skipping the "
            + "caching decorator would reintroduce #495's double-fetch");
        toolProvider.Should().NotBeOfType<BehaviorRecordingMcpToolProvider>();
        toolProvider.Should().NotBeOfType<McpToolProvider>();
    }

    [Fact]
    public async Task AddMcpClientDependencies_WithoutABehaviorRegistry_FailsToResolveRatherThanSkippingTheRecording()
    {
        // The mirror of the scanner test below, and the reason the scanning decorator is still known
        // to be in the chain even though it is no longer the outermost type: both decorators are built
        // by the same factory, so a missing collaborator of either one fails the resolution.
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.Configure<AIConfig>(_ => { });
        services.Configure<Domain.Common.Config.MetaHarness.MetaHarnessConfig>(_ => { });
        services.AddSingleton(TestSsrf.HandlerFactory());
        services.AddSingleton(Mock.Of<IMcpSecurityScanner>());
        services.AddSingleton(Mock.Of<IAmbientRequestScope>());
        services.AddSingleton(Mock.Of<Application.AI.Common.Interfaces.Egress.IEgressAuditWriter>());
        // Deliberately no IToolBehaviorRegistry.

        services.AddMcpClientDependencies();
        await using var provider = services.BuildServiceProvider();

        var resolve = () => provider.GetService<IMcpToolProvider>();

        resolve.Should().Throw<InvalidOperationException>(
            "a host that publishes external tools without recording what they declared leaves the "
            + "behaviour posture with nothing to decide from");
    }

    [Fact]
    public async Task AddMcpClientDependencies_WithoutASecurityScanner_FailsToResolveRatherThanSkippingTheScan()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.Configure<AIConfig>(_ => { });
        services.Configure<Domain.Common.Config.MetaHarness.MetaHarnessConfig>(_ => { });
        services.AddSingleton(TestSsrf.HandlerFactory());
        services.AddSingleton(Mock.Of<IAmbientRequestScope>());
        services.AddSingleton(Mock.Of<Application.AI.Common.Interfaces.Egress.IEgressAuditWriter>());
        // Deliberately no IMcpSecurityScanner — this is the ungoverned-host composition.

        services.AddMcpClientDependencies();
        // Async disposal: McpConnectionManager is IAsyncDisposable and the container refuses to
        // release it synchronously.
        await using var provider = services.BuildServiceProvider();

        var resolve = () => provider.GetService<IMcpToolProvider>();

        resolve.Should().Throw<InvalidOperationException>(
            "a host that never wired the governance layer must fail loudly rather than silently "
            + "publishing unscanned tool descriptions into the model's context");
    }

    [Fact]
    public void AddMcpClientDependencies_RegistersTraceResourceProvider()
    {
        var provider = BuildProvider();

        var resourceProvider = provider.GetService<TraceResourceProvider>();

        resourceProvider.Should().NotBeNull();
    }

    [Fact]
    public void AddMcpClientDependencies_RegistersIMcpResourceProvider()
    {
        var provider = BuildProvider();

        var resourceProvider = provider.GetService<IMcpResourceProvider>();

        resourceProvider.Should().NotBeNull();
        resourceProvider.Should().BeOfType<TraceResourceProvider>();
    }

    [Fact]
    public void AddMcpClientDependencies_McpConnectionManagerIsSingleton()
    {
        var provider = BuildProvider();

        var first = provider.GetService<McpConnectionManager>();
        var second = provider.GetService<McpConnectionManager>();

        first.Should().BeSameAs(second);
    }

    [Fact]
    public void AddMcpClientDependencies_IMcpToolProviderIsSingleton()
    {
        var provider = BuildProvider();

        var first = provider.GetService<IMcpToolProvider>();
        var second = provider.GetService<IMcpToolProvider>();

        first.Should().BeSameAs(second);
    }

    [Fact]
    public void AddMcpClientDependencies_ReturnsSameServiceCollection()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.Configure<AIConfig>(_ => { });
        services.Configure<Domain.Common.Config.MetaHarness.MetaHarnessConfig>(_ => { });

        var result = services.AddMcpClientDependencies();

        result.Should().BeSameAs(services);
    }
}
