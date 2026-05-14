using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Resilience;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Resilience;
using FluentAssertions;
using Infrastructure.AI.Resilience;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Resilience;

/// <summary>
/// Tests for <see cref="ResilientChatClientProvider"/> — the composition root that
/// assembles the fallback chain from config, wires resilience pipelines, and returns
/// a <see cref="ResilientChatClient"/>. Validates config-driven composition, caching,
/// and the disabled-resilience bypass.
/// </summary>
public sealed class ResilientChatClientProviderTests : IAsyncDisposable
{
    private readonly Mock<IChatClientFactory> _chatClientFactory = new();
    private readonly PollyProviderHealthMonitor _healthMonitor = new(null);
    private readonly ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;
    private readonly ILogger<ResilientChatClientProvider> _logger =
        NullLoggerFactory.Instance.CreateLogger<ResilientChatClientProvider>();

    private readonly List<IChatClient> _createdClients = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _createdClients)
            client.Dispose();
    }

    [Fact]
    public async Task GetResilientChatClientAsync_BuildsChainFromConfig()
    {
        var config = CreateEnabledConfig(
            ("AzureOpenAI", "gpt-4o"),
            ("AzureOpenAI", "gpt-35-turbo"));

        SetupFactoryDefaults();
        var sut = CreateProvider(config);

        var client = await sut.GetResilientChatClientAsync();

        client.Should().NotBeNull();
        client.Should().BeOfType<ResilientChatClient>();
        _chatClientFactory.Verify(
            f => f.GetChatClientAsync(AIAgentFrameworkClientType.AzureOpenAI, "gpt-4o", It.IsAny<CancellationToken>()),
            Times.Once);
        _chatClientFactory.Verify(
            f => f.GetChatClientAsync(AIAgentFrameworkClientType.AzureOpenAI, "gpt-35-turbo", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetResilientChatClientAsync_CachesResult()
    {
        var config = CreateEnabledConfig(("AzureOpenAI", "gpt-4o"));
        SetupFactoryDefaults();
        var sut = CreateProvider(config);

        var first = await sut.GetResilientChatClientAsync();
        var second = await sut.GetResilientChatClientAsync();

        ReferenceEquals(first, second).Should().BeTrue();
        _chatClientFactory.Verify(
            f => f.GetChatClientAsync(It.IsAny<AIAgentFrameworkClientType>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetResilientChatClientAsync_EmptyChain_ThrowsInvalidOperation()
    {
        var config = new ResilienceConfig { Enabled = true, FallbackChain = [] };
        var sut = CreateProvider(config);

        var act = () => sut.GetResilientChatClientAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*FallbackChain*empty*");
    }

    [Fact]
    public async Task GetResilientChatClientAsync_FactoryFailure_PropagatesException()
    {
        var config = CreateEnabledConfig(("AzureOpenAI", "gpt-4o"));
        _chatClientFactory
            .Setup(f => f.GetChatClientAsync(It.IsAny<AIAgentFrameworkClientType>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Provider not configured"));

        var sut = CreateProvider(config);

        var act = () => sut.GetResilientChatClientAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Provider not configured");
    }

    [Fact]
    public async Task GetResilientChatClientAsync_Disabled_ReturnsRawClient()
    {
        var rawClient = CreateMockChatClient();
        _chatClientFactory
            .Setup(f => f.GetChatClientAsync(AIAgentFrameworkClientType.AzureOpenAI, "gpt-4o", It.IsAny<CancellationToken>()))
            .ReturnsAsync(rawClient);

        var config = new ResilienceConfig
        {
            Enabled = false,
            FallbackChain =
            [
                new FallbackProviderConfig { ClientType = AIAgentFrameworkClientType.AzureOpenAI, DeploymentId = "gpt-4o" }
            ]
        };
        var sut = CreateProvider(config);

        var client = await sut.GetResilientChatClientAsync();

        client.Should().BeSameAs(rawClient);
        client.Should().NotBeOfType<ResilientChatClient>();
    }

    [Fact]
    public async Task GetResilientChatClientAsync_Disabled_EmptyChain_ThrowsInvalidOperation()
    {
        var config = new ResilienceConfig { Enabled = false, FallbackChain = [] };
        var sut = CreateProvider(config);

        var act = () => sut.GetResilientChatClientAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*FallbackChain*empty*");
    }

    private ResilientChatClientProvider CreateProvider(ResilienceConfig config)
    {
        var configMonitor = Mock.Of<IOptionsMonitor<ResilienceConfig>>(
            m => m.CurrentValue == config);

        return new ResilientChatClientProvider(
            _chatClientFactory.Object,
            configMonitor,
            _healthMonitor,
            _loggerFactory,
            _logger);
    }

    private void SetupFactoryDefaults()
    {
        _chatClientFactory
            .Setup(f => f.GetChatClientAsync(It.IsAny<AIAgentFrameworkClientType>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => CreateMockChatClient());
    }

    private IChatClient CreateMockChatClient()
    {
        var mock = new Mock<IChatClient>();
        _createdClients.Add(mock.Object);
        return mock.Object;
    }

    private static ResilienceConfig CreateEnabledConfig(params (string clientType, string deploymentId)[] providers)
    {
        return new ResilienceConfig
        {
            Enabled = true,
            FallbackChain = providers.Select(p => new FallbackProviderConfig
            {
                ClientType = Enum.Parse<AIAgentFrameworkClientType>(p.clientType),
                DeploymentId = p.deploymentId
            }).ToArray(),
            Retry = new RetryConfig { MaxAttempts = 2, BaseDelaySeconds = 0.01, BackoffType = "Exponential" },
            CircuitBreaker = new CircuitBreakerConfig
            {
                FailureRatio = 0.5,
                SamplingDurationSeconds = 30,
                MinimumThroughput = 5,
                BreakDurationSeconds = 60
            },
            Timeout = new TimeoutConfig { PerAttemptSeconds = 30 }
        };
    }
}
