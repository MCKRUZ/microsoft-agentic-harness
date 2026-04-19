using Domain.Common.Config;
using Domain.Common.Config.Cache;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Presentation.Common.Extensions;
using Xunit;

namespace Presentation.Common.Tests.Extensions;

/// <summary>
/// Integration tests for <see cref="IServiceCollectionExtensions"/> covering
/// config section registration, cache configuration, and auth dependency wiring.
/// </summary>
public sealed class IServiceCollectionExtensionsTests
{
    // -- RegisterConfigSections --

    [Fact]
    public void RegisterConfigSections_BindsAppConfigSection()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AppConfig:Common:ApplicationName"] = "TestApp"
            })
            .Build();

        services.RegisterConfigSections(config);
        var provider = services.BuildServiceProvider();

        var appConfig = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<AppConfig>>().Value;
        appConfig.Common.ApplicationName.Should().Be("TestApp");
    }

    [Fact]
    public void RegisterConfigSections_BindsAllSubsections()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AppConfig:Common:ApplicationVersion"] = "2.0",
                ["AppConfig:Logging:PipeName"] = "test-pipe",
                ["AppConfig:Cache:CacheType"] = "Memory"
            })
            .Build();

        services.RegisterConfigSections(config);
        var provider = services.BuildServiceProvider();

        var commonConfig = provider.GetRequiredService<
            Microsoft.Extensions.Options.IOptions<Domain.Common.Config.CommonConfig>>().Value;
        commonConfig.ApplicationVersion.Should().Be("2.0");

        var loggingConfig = provider.GetRequiredService<
            Microsoft.Extensions.Options.IOptions<Domain.Common.Config.LoggingConfig>>().Value;
        loggingConfig.PipeName.Should().Be("test-pipe");
    }

    [Fact]
    public void RegisterConfigSections_ReturnsSameServiceCollection()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();

        var result = services.RegisterConfigSections(config);

        result.Should().BeSameAs(services);
    }

    // -- AddCacheConfiguration --

    [Fact]
    public void AddCacheConfiguration_MemoryCache_RegistersMemoryAndDistributed()
    {
        var services = new ServiceCollection();
        var cacheConfig = new CacheConfig { CacheType = CacheType.Memory };

        services.AddCacheConfiguration(cacheConfig);
        var provider = services.BuildServiceProvider();

        provider.GetService<IMemoryCache>().Should().NotBeNull();
        provider.GetService<IDistributedCache>().Should().NotBeNull();
    }

    [Fact]
    public void AddCacheConfiguration_NoneCache_RegistersMemoryAndDistributed()
    {
        var services = new ServiceCollection();
        var cacheConfig = new CacheConfig { CacheType = CacheType.None };

        services.AddCacheConfiguration(cacheConfig);
        var provider = services.BuildServiceProvider();

        provider.GetService<IMemoryCache>().Should().NotBeNull();
        provider.GetService<IDistributedCache>().Should().NotBeNull();
    }

    [Fact]
    public void AddCacheConfiguration_DistributedMemory_RegistersDistributedCache()
    {
        var services = new ServiceCollection();
        var cacheConfig = new CacheConfig { CacheType = CacheType.DistributedMemory };

        services.AddCacheConfiguration(cacheConfig);
        var provider = services.BuildServiceProvider();

        provider.GetService<IDistributedCache>().Should().NotBeNull();
    }

    [Fact]
    public void AddCacheConfiguration_ReturnsSameServiceCollection()
    {
        var services = new ServiceCollection();
        var cacheConfig = new CacheConfig();

        var result = services.AddCacheConfiguration(cacheConfig);

        result.Should().BeSameAs(services);
    }

    // -- AddAuthDependencies --

    [Fact]
    public void AddAuthDependencies_NoB2CInstance_RegistersBasicAuth()
    {
        var services = new ServiceCollection();
        var azureConfig = new Domain.Common.Config.Azure.AzureConfig();

        services.AddAuthDependencies(azureConfig);

        services.Any(d => d.ServiceType.Name.Contains("Authorization")).Should().BeTrue();
        services.Any(d => d.ServiceType.Name.Contains("Authentication")).Should().BeTrue();
    }

    [Fact]
    public void AddAuthDependencies_ReturnsSameServiceCollection()
    {
        var services = new ServiceCollection();
        var azureConfig = new Domain.Common.Config.Azure.AzureConfig();

        var result = services.AddAuthDependencies(azureConfig);

        result.Should().BeSameAs(services);
    }
}
