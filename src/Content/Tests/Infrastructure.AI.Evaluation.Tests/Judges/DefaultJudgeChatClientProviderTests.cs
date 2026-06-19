using Application.AI.Common.Evaluation.Models;
using Application.AI.Common.Interfaces;
using Domain.Common.Config.AI;
using FluentAssertions;
using Infrastructure.AI.Evaluation.Judges;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Evaluation.Tests.Judges;

public sealed class DefaultJudgeChatClientProviderTests
{
    private static IOptionsMonitor<JudgeOptions> Options(JudgeOptions value)
    {
        var monitor = new Mock<IOptionsMonitor<JudgeOptions>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(value);
        return monitor.Object;
    }

    [Fact]
    public async Task GetJudgeAsync_throws_when_deployment_unset()
    {
        var factory = new Mock<IChatClientFactory>(MockBehavior.Strict);
        var sut = new DefaultJudgeChatClientProvider(factory.Object, Options(new JudgeOptions()));

        var act = () => sut.GetJudgeAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Deployment*");
        factory.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetJudgeAsync_uses_configured_client_type_when_set()
    {
        var client = Mock.Of<IChatClient>();
        var factory = new Mock<IChatClientFactory>();
        factory.Setup(f => f.GetChatClientAsync(
                AIAgentFrameworkClientType.OpenAI,
                "gpt-4o-mini",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(client);

        var sut = new DefaultJudgeChatClientProvider(
            factory.Object,
            Options(new JudgeOptions { ClientType = AIAgentFrameworkClientType.OpenAI, Deployment = "gpt-4o-mini" }));

        var result = await sut.GetJudgeAsync(CancellationToken.None);

        result.Should().BeSameAs(client);
    }

    [Fact]
    public async Task GetJudgeAsync_falls_back_to_first_available_provider_when_client_type_unset()
    {
        var client = Mock.Of<IChatClient>();
        var factory = new Mock<IChatClientFactory>();
        factory.Setup(f => f.GetAvailableProviders()).Returns(new Dictionary<AIAgentFrameworkClientType, bool>
        {
            [AIAgentFrameworkClientType.AzureOpenAI] = false,
            [AIAgentFrameworkClientType.OpenAI] = true,
            [AIAgentFrameworkClientType.PersistentAgents] = true
        });
        factory.Setup(f => f.GetChatClientAsync(
                AIAgentFrameworkClientType.OpenAI,
                "judge-deployment",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(client);

        var sut = new DefaultJudgeChatClientProvider(
            factory.Object,
            Options(new JudgeOptions { Deployment = "judge-deployment" }));

        var result = await sut.GetJudgeAsync(CancellationToken.None);

        result.Should().BeSameAs(client);
    }

    [Fact]
    public async Task GetJudgeAsync_throws_when_no_provider_available_and_client_type_unset()
    {
        var factory = new Mock<IChatClientFactory>();
        factory.Setup(f => f.GetAvailableProviders()).Returns(new Dictionary<AIAgentFrameworkClientType, bool>
        {
            [AIAgentFrameworkClientType.AzureOpenAI] = false,
            [AIAgentFrameworkClientType.OpenAI] = false
        });

        var sut = new DefaultJudgeChatClientProvider(
            factory.Object,
            Options(new JudgeOptions { Deployment = "something" }));

        var act = () => sut.GetJudgeAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*No AI provider*");
    }

    [Fact]
    public async Task GetJudgeAsync_caches_client_by_client_type_and_deployment()
    {
        var client = Mock.Of<IChatClient>();
        var factory = new Mock<IChatClientFactory>();
        factory.Setup(f => f.GetChatClientAsync(
                It.IsAny<AIAgentFrameworkClientType>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(client);

        var sut = new DefaultJudgeChatClientProvider(
            factory.Object,
            Options(new JudgeOptions { ClientType = AIAgentFrameworkClientType.OpenAI, Deployment = "x" }));

        var first = await sut.GetJudgeAsync(CancellationToken.None);
        var second = await sut.GetJudgeAsync(CancellationToken.None);
        var third = await sut.GetJudgeAsync(CancellationToken.None);

        first.Should().BeSameAs(second).And.BeSameAs(third);
        factory.Verify(f => f.GetChatClientAsync(
            AIAgentFrameworkClientType.OpenAI, "x", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetJudgeAsync_overload_resolves_explicit_client_type_and_deployment()
    {
        var client = Mock.Of<IChatClient>();
        var factory = new Mock<IChatClientFactory>();
        factory.Setup(f => f.GetChatClientAsync(
                AIAgentFrameworkClientType.AzureOpenAI,
                "panel-model",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(client);

        var sut = new DefaultJudgeChatClientProvider(
            factory.Object,
            Options(new JudgeOptions { Deployment = "default" }));

        var result = await sut.GetJudgeAsync(
            AIAgentFrameworkClientType.AzureOpenAI, "panel-model", CancellationToken.None);

        result.Should().BeSameAs(client);
    }

    [Fact]
    public async Task GetJudgeAsync_overload_throws_when_deployment_blank()
    {
        var factory = new Mock<IChatClientFactory>(MockBehavior.Strict);
        var sut = new DefaultJudgeChatClientProvider(
            factory.Object,
            Options(new JudgeOptions { Deployment = "default" }));

        var act = () => sut.GetJudgeAsync(AIAgentFrameworkClientType.OpenAI, "   ", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*deployment*");
        factory.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetJudgeAsync_overload_shares_cache_with_default_for_same_model()
    {
        var client = Mock.Of<IChatClient>();
        var factory = new Mock<IChatClientFactory>();
        factory.Setup(f => f.GetChatClientAsync(
                It.IsAny<AIAgentFrameworkClientType>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(client);

        var sut = new DefaultJudgeChatClientProvider(
            factory.Object,
            Options(new JudgeOptions { ClientType = AIAgentFrameworkClientType.OpenAI, Deployment = "shared" }));

        var viaDefault = await sut.GetJudgeAsync(CancellationToken.None);
        var viaOverload = await sut.GetJudgeAsync(
            AIAgentFrameworkClientType.OpenAI, "shared", CancellationToken.None);

        viaDefault.Should().BeSameAs(viaOverload);
        factory.Verify(f => f.GetChatClientAsync(
            AIAgentFrameworkClientType.OpenAI, "shared", It.IsAny<CancellationToken>()), Times.Once);
    }
}
