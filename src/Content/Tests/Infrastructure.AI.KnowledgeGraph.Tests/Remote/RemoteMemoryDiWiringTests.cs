using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.Routing;
using Application.AI.Common.Prompts.Interfaces;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using FluentAssertions;
using Infrastructure.AI.KnowledgeGraph.Memory;
using Infrastructure.AI.KnowledgeGraph.Remote;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.KnowledgeGraph.Tests.Remote;

/// <summary>
/// Regression tests exercising the real dependency-injection path for the four remote
/// memory-hosting seams that live in <c>Infrastructure.AI.KnowledgeGraph</c> — proving that
/// <c>AppConfig:AI:RemoteMemory:Enabled</c> actually swaps the resolved implementation, in both
/// directions, and that the named <c>IHttpClientFactory</c> client is configured from
/// <see cref="RemoteMemoryConfig"/>.
/// </summary>
public sealed class RemoteMemoryDiWiringTests
{
    [Fact]
    public void RemoteMemoryEnabled_ResolvesRemoteImplementations_ForAllFourSeams()
    {
        using var provider = BuildProvider(enabled: true);
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IConversationFactExtractor>()
            .Should().BeOfType<RemoteConversationFactExtractor>();
        scope.ServiceProvider.GetRequiredService<IKnowledgeMemory>()
            .Should().BeOfType<RemoteKnowledgeMemory>();
        scope.ServiceProvider.GetRequiredService<IMemoryAbstractor>()
            .Should().BeOfType<RemoteMemoryAbstractor>();
        scope.ServiceProvider.GetRequiredService<IMemoryConsolidator>()
            .Should().BeOfType<RemoteMemoryConsolidator>();
    }

    [Fact]
    public void RemoteMemoryDisabled_ResolvesLocalImplementations_ForAllFourSeams()
    {
        using var provider = BuildProvider(enabled: false);
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IConversationFactExtractor>()
            .Should().BeOfType<ConversationFactExtractor>();
        scope.ServiceProvider.GetRequiredService<IKnowledgeMemory>()
            .Should().BeOfType<KnowledgeMemoryService>();
        scope.ServiceProvider.GetRequiredService<IMemoryAbstractor>()
            .Should().NotBeOfType<RemoteMemoryAbstractor>(
                "the fail-fast NotConfigured default must win when remote memory is off");
        scope.ServiceProvider.GetRequiredService<IMemoryConsolidator>()
            .Should().NotBeOfType<RemoteMemoryConsolidator>();
    }

    [Fact]
    public void RemoteMemoryEnabled_ConfiguresNamedHttpClient_WithBaseAddressAndApiKeyHeader()
    {
        using var provider = BuildProvider(enabled: true, baseUrl: "https://avatar.example.com/", avatarId: "sage-1", apiKey: "secret-key");

        var factory = provider.GetRequiredService<IHttpClientFactory>();
        using var client = factory.CreateClient(RemoteMemoryHttpClientNames.ClientName);

        client.BaseAddress.Should().Be(new Uri("https://avatar.example.com/api/v1/harness-memory/sage-1/"));
        client.DefaultRequestHeaders.GetValues("X-Api-Key").Should().ContainSingle().Which.Should().Be("secret-key");
    }

    [Fact]
    public void RemoteMemoryDisabled_DoesNotRegisterTheNamedHttpClient()
    {
        using var provider = BuildProvider(enabled: false);

        var factory = provider.GetRequiredService<IHttpClientFactory>();
        // An unregistered named client still resolves (IHttpClientFactory never throws on an
        // unknown name), but it carries none of RemoteMemoryConfig's configuration.
        using var client = factory.CreateClient(RemoteMemoryHttpClientNames.ClientName);

        client.BaseAddress.Should().BeNull();
    }

    [Fact]
    public void HotConfigReloadToHttp_RefusesToCreateTheClient()
    {
        // The named client's configure delegate re-reads IOptionsMonitor on every CreateClient
        // call, so a config value that goes from https:// to http:// after boot (a hot reload) must
        // still be refused — RemoteMemoryConfigValidator's ValidateOnStart only ever checks the
        // value at boot, not on every later reload.
        var config = new AppConfig();
        config.AI.Rag.GraphRag.GraphProvider = "in_memory";
        config.AI.RemoteMemory.Enabled = true;
        config.AI.RemoteMemory.BaseUrl = "https://avatar.example.com";
        config.AI.RemoteMemory.AvatarId = "avatar-1";
        config.AI.RemoteMemory.ApiKey = "key";

        using var provider = BuildProviderForConfig(config);
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        using (var client = factory.CreateClient(RemoteMemoryHttpClientNames.ClientName))
        {
            client.BaseAddress.Should().NotBeNull("https:// must succeed before the simulated reload");
        }

        config.AI.RemoteMemory.BaseUrl = "http://avatar.example.com";

        var act = () => factory.CreateClient(RemoteMemoryHttpClientNames.ClientName);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*https*", "a reloaded http:// BaseUrl must never silently start sending the API key and transcripts in cleartext");
    }

    private static ServiceProvider BuildProvider(
        bool enabled, string baseUrl = "https://avatar.example.com", string avatarId = "avatar-1", string apiKey = "key")
    {
        var config = new AppConfig();
        config.AI.Rag.GraphRag.GraphProvider = "in_memory";
        config.AI.RemoteMemory.Enabled = enabled;
        config.AI.RemoteMemory.BaseUrl = baseUrl;
        config.AI.RemoteMemory.AvatarId = avatarId;
        config.AI.RemoteMemory.ApiKey = apiKey;

        return BuildProviderForConfig(config);
    }

    private static ServiceProvider BuildProviderForConfig(AppConfig config)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();
        services.AddSingleton(Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == config));
        services.AddSingleton(Mock.Of<IModelRouter>());
        services.AddSingleton(Mock.Of<IAgentExecutionContext>());
        services.AddSingleton(Mock.Of<IPromptRegistry>());
        services.AddSingleton(Mock.Of<IPromptRenderer>());
        services.AddSingleton(Mock.Of<IPromptUsageRecorder>());

        services.AddKnowledgeGraphDependencies(config);

        return services.BuildServiceProvider();
    }
}
