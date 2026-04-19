using Application.AI.Common.Interfaces;
using Domain.Common.Config.AI;
using FluentAssertions;
using Infrastructure.AI.MCP.Resources;
using Infrastructure.AI.MCP.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
    public void AddMcpClientDependencies_RegistersIMcpToolProvider()
    {
        var provider = BuildProvider();

        var toolProvider = provider.GetService<IMcpToolProvider>();

        toolProvider.Should().NotBeNull();
        toolProvider.Should().BeOfType<McpToolProvider>();
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
