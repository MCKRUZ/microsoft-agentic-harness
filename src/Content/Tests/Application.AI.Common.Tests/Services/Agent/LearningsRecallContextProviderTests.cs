using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Learnings;
using Application.AI.Common.Services.Agent;
using Domain.AI.Learnings;
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
/// Tests the recall logic of <see cref="LearningsRecallContextProvider"/> in isolation.
/// </summary>
/// <remarks>
/// These assert what the provider <em>contributes</em>, which is deliberately only the recalled block —
/// the framework's additive merge is what combines it with the incoming instructions. That the merge
/// produces exactly one copy of each is asserted separately in
/// <see cref="AIContextProviderMergeContractTests"/>, which drives the public
/// <see cref="AIContextProvider.InvokingAsync"/> entry point.
/// </remarks>
public class LearningsRecallContextProviderTests
{
    private static AIContext ContextWithUserMessage(string text, string? instructions = null) => new()
    {
        Instructions = instructions,
        Messages = new List<ChatMessage> { new(ChatRole.User, text) }
    };

    private static WeightedLearning Lesson(string content) => new()
    {
        Learning = new LearningEntry
        {
            LearningId = Guid.NewGuid(),
            Category = LearningCategory.ToolUsagePattern,
            DecayClass = DecayClass.Stable,
            Scope = new LearningScope { IsGlobal = true },
            Content = content,
            Source = new LearningSource
            {
                SourceType = LearningSourceType.AgentSelfImprovement,
                SourceId = "run-1",
                SourceDescription = "synthesis"
            },
            Provenance = new LearningProvenance
            {
                OriginPipeline = "work_memory_synthesis",
                OriginTask = "overnight_synthesis",
                OriginTimestamp = DateTimeOffset.UtcNow,
                Confidence = 0.9
            },
            CreatedAt = DateTimeOffset.UtcNow
        },
        RelevanceScore = 0.8,
        FeedbackScore = 1.0,
        FreshnessScore = 1.0,
        FinalScore = 0.85
    };

    private static LearningsRecallContextProvider Build(
        ILearningRecaller? recaller,
        bool enabled = true,
        bool withScope = true,
        int maxResults = 3,
        double minRelevance = 0.3)
    {
        IServiceProvider? scopeProvider = null;
        if (withScope)
        {
            var services = new ServiceCollection();
            if (recaller is not null)
                services.AddSingleton(recaller);
            scopeProvider = services.BuildServiceProvider();
        }

        var ambient = Mock.Of<IAmbientRequestScope>(a => a.Current == scopeProvider);
        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                LearningsRecall = new LearningsRecallConfig
                {
                    Enabled = enabled,
                    MaxResults = maxResults,
                    MinRelevance = minRelevance
                }
            }
        };
        var monitor = Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == appConfig);
        return new LearningsRecallContextProvider(
            ambient, monitor, NullLogger<LearningsRecallContextProvider>.Instance);
    }

    [Fact]
    public async Task RecallBlock_WithRelevantLessons_ReturnsOnlyTheRecalledLessons()
    {
        var recaller = new Mock<ILearningRecaller>();
        recaller.Setup(r => r.RecallAsync("how do I deploy?", 3, 0.3, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Lesson("Build from the worktree path, not main root."), Lesson("Verify tests before reporting done.") });
        var sut = Build(recaller.Object);
        var input = ContextWithUserMessage("how do I deploy?", instructions: "You are helpful.");

        var block = await sut.RecallBlockAsync(input);

        block.Should().NotBeNull();
        block.Should().Contain("Lessons from past work");
        block.Should().Contain("Build from the worktree path, not main root.");
        block.Should().Contain("Verify tests before reporting done.");
        // The incoming prompt must NOT be echoed back: the base merge re-adds it, so returning it here
        // would send the whole system prompt to the model twice.
        block.Should().NotContain("You are helpful.");
    }

    [Fact]
    public async Task RecallBlock_PassesConfiguredMaxResultsAndMinRelevance()
    {
        var recaller = new Mock<ILearningRecaller>();
        recaller.Setup(r => r.RecallAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<WeightedLearning>());
        var sut = Build(recaller.Object, maxResults: 7, minRelevance: 0.55);

        await sut.RecallBlockAsync(ContextWithUserMessage("task"));

        recaller.Verify(r => r.RecallAsync("task", 7, 0.55, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RecallBlock_Disabled_ContributesNothing()
    {
        var recaller = new Mock<ILearningRecaller>(MockBehavior.Strict);
        var sut = Build(recaller.Object, enabled: false);

        var block = await sut.RecallBlockAsync(ContextWithUserMessage("anything"));

        block.Should().BeNull();
        recaller.Verify(r => r.RecallAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<double>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RecallBlock_NoAmbientScope_ContributesNothing()
    {
        var sut = Build(recaller: null, withScope: false);

        var block = await sut.RecallBlockAsync(ContextWithUserMessage("anything"));

        block.Should().BeNull();
    }

    [Fact]
    public async Task RecallBlock_NoUserMessage_ContributesNothing()
    {
        var recaller = new Mock<ILearningRecaller>(MockBehavior.Strict);
        var sut = Build(recaller.Object);
        var input = new AIContext { Messages = new List<ChatMessage> { new(ChatRole.System, "system only") } };

        var block = await sut.RecallBlockAsync(input);

        block.Should().BeNull();
        recaller.Verify(r => r.RecallAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<double>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RecallBlock_NoRelevantLessons_ContributesNothing()
    {
        var recaller = new Mock<ILearningRecaller>();
        recaller.Setup(r => r.RecallAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<WeightedLearning>());
        var sut = Build(recaller.Object);

        var block = await sut.RecallBlockAsync(ContextWithUserMessage("anything", instructions: "keep me"));

        block.Should().BeNull();
    }

    [Fact]
    public async Task RecallBlock_RecallerThrows_ContributesNothing()
    {
        var recaller = new Mock<ILearningRecaller>();
        recaller.Setup(r => r.RecallAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("recall down"));
        var sut = Build(recaller.Object);

        var block = await sut.RecallBlockAsync(ContextWithUserMessage("anything", instructions: "keep me"));

        block.Should().BeNull();
    }

    [Fact]
    public async Task RecallBlock_NoExistingInstructions_StillReturnsOnlyTheBlock()
    {
        var recaller = new Mock<ILearningRecaller>();
        recaller.Setup(r => r.RecallAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Lesson("Prefer the worktree build.") });
        var sut = Build(recaller.Object);

        var block = await sut.RecallBlockAsync(ContextWithUserMessage("how?")); // no instructions

        block.Should().StartWith("## Lessons from past work");
        block.Should().Contain("Prefer the worktree build.");
    }
}
