using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Governance;
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
    public void AddMcpClientDependencies_PublishesTheScanningDecoratorAsIMcpToolProvider()
    {
        var provider = BuildProvider();

        var toolProvider = provider.GetService<IMcpToolProvider>();

        toolProvider.Should().NotBeNull();
        toolProvider.Should().BeOfType<ScanningMcpToolProvider>(
            "every consumer resolves the interface, so the decorator has to be what the interface "
            + "returns — registering the bare transport provider would leave tool definitions from "
            + "external servers unscanned");
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
