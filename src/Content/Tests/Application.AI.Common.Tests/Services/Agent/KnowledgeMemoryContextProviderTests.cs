using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Services.Agent;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using FluentAssertions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Services.Agent;

/// <summary>
/// Tests the recall logic of <see cref="KnowledgeMemoryContextProvider"/> in isolation.
/// </summary>
/// <remarks>
/// These assert what the provider <em>contributes</em>, which is deliberately only the recalled block —
/// the framework's additive merge is what combines it with the incoming instructions. That the merge
/// produces exactly one copy of each is asserted separately in
/// <see cref="AIContextProviderMergeContractTests"/>, which drives the public
/// <see cref="AIContextProvider.InvokingAsync"/> entry point.
/// </remarks>
public class KnowledgeMemoryContextProviderTests
{
    private static AIContext ContextWithUserMessage(string text, string? instructions = null) => new()
    {
        Instructions = instructions,
        Messages = new List<ChatMessage> { new(ChatRole.User, text) }
    };

    private static GraphNode Fact(string content) => new()
    {
        Id = $"memory:{content.GetHashCode()}",
        Name = "fact-key",
        Type = "Fact",
        Properties = new Dictionary<string, string> { ["content"] = content }
    };

    private static KnowledgeMemoryContextProvider Build(
        IKnowledgeMemory? memory,
        bool enabled = true,
        bool withScope = true)
    {
        IServiceProvider? scopeProvider = null;
        if (withScope)
        {
            var services = new ServiceCollection();
            if (memory is not null)
                services.AddSingleton(memory);
            scopeProvider = services.BuildServiceProvider();
        }

        var ambient = Mock.Of<IAmbientRequestScope>(a => a.Current == scopeProvider);
        var appConfig = new AppConfig { AI = new AIConfig { KnowledgeBridge = new KnowledgeBridgeConfig { Enabled = enabled } } };
        var monitor = Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == appConfig);
        return new KnowledgeMemoryContextProvider(
            ambient, monitor, NullLogger<KnowledgeMemoryContextProvider>.Instance);
    }

    [Fact]
    public async Task RecallBlock_WithRelevantFacts_ReturnsOnlyTheRecalledFacts()
    {
        var memory = new Mock<IKnowledgeMemory>();
        memory.Setup(m => m.RecallAsync("what theme do I like?", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Fact("The user prefers dark mode."), Fact("The user is based in NYC.") });
        var sut = Build(memory.Object);
        var input = ContextWithUserMessage("what theme do I like?", instructions: "You are helpful.");

        var block = await sut.RecallBlockAsync(input);

        block.Should().NotBeNull();
        block.Should().Contain("Relevant remembered context");
        block.Should().Contain("The user prefers dark mode.");
        block.Should().Contain("The user is based in NYC.");
        // The incoming prompt must NOT be echoed back: the base merge re-adds it, so returning it here
        // would send the whole system prompt to the model twice.
        block.Should().NotContain("You are helpful.");
    }

    [Fact]
    public async Task RecallBlock_Disabled_ContributesNothing()
    {
        var memory = new Mock<IKnowledgeMemory>(MockBehavior.Strict);
        var sut = Build(memory.Object, enabled: false);

        var block = await sut.RecallBlockAsync(ContextWithUserMessage("anything"));

        block.Should().BeNull();
        memory.Verify(m => m.RecallAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RecallBlock_NoAmbientScope_ContributesNothing()
    {
        // No request scope established (e.g. background work) → cannot resolve tenant-aware memory.
        var sut = Build(memory: null, withScope: false);

        var block = await sut.RecallBlockAsync(ContextWithUserMessage("anything"));

        block.Should().BeNull();
    }

    [Fact]
    public async Task RecallBlock_NoUserMessage_ContributesNothing()
    {
        var memory = new Mock<IKnowledgeMemory>(MockBehavior.Strict);
        var sut = Build(memory.Object);
        var input = new AIContext { Messages = new List<ChatMessage> { new(ChatRole.System, "system only") } };

        var block = await sut.RecallBlockAsync(input);

        block.Should().BeNull();
        memory.Verify(m => m.RecallAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RecallBlock_NoRelevantFacts_ContributesNothing()
    {
        var memory = new Mock<IKnowledgeMemory>();
        memory.Setup(m => m.RecallAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<GraphNode>());
        var sut = Build(memory.Object);

        var block = await sut.RecallBlockAsync(ContextWithUserMessage("anything", instructions: "keep me"));

        block.Should().BeNull();
    }

    [Fact]
    public async Task RecallBlock_MemoryThrows_ContributesNothing()
    {
        // Memory is an enhancement, never a hard dependency: a recall failure must not break the turn.
        var memory = new Mock<IKnowledgeMemory>();
        memory.Setup(m => m.RecallAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("graph down"));
        var sut = Build(memory.Object);

        var block = await sut.RecallBlockAsync(ContextWithUserMessage("anything", instructions: "keep me"));

        block.Should().BeNull();
    }

    [Fact]
    public async Task RecallBlock_NoExistingInstructions_StillReturnsOnlyTheBlock()
    {
        var memory = new Mock<IKnowledgeMemory>();
        memory.Setup(m => m.RecallAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Fact("Likes terse answers.") });
        var sut = Build(memory.Object);

        var block = await sut.RecallBlockAsync(ContextWithUserMessage("style?")); // no instructions

        block.Should().StartWith("## Relevant remembered context");
        block.Should().Contain("Likes terse answers.");
    }
}
