using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Routing;
using Application.AI.Common.Interfaces.Skills;
using Application.AI.Common.MediatRBehaviors;
using Application.Core.CQRS.Agents.ExecuteAgentTurn;
using Domain.AI.Routing.Enums;
using Domain.AI.Routing.Models;
using Domain.Common.Config;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.MediatRBehaviors;

/// <summary>
/// Tests for <see cref="SkillEffectivenessTrackingBehavior{TRequest, TResponse}"/> — the write half
/// of the procedural-memory loop (#695). Mirrors <see cref="WorkEpisodeCaptureBehaviorTests"/>'s
/// fire-and-forget polling pattern.
/// </summary>
public sealed class SkillEffectivenessTrackingBehaviorTests
{
    private static readonly TimeSpan RecordTimeout = TimeSpan.FromSeconds(30);

    private readonly Mock<IRequestIntentClassifier> _classifier = new();
    private readonly Mock<ISkillEffectivenessTracker> _tracker = new();
    private readonly AppConfig _appConfig = new();

    public SkillEffectivenessTrackingBehaviorTests()
    {
        // GraphRagConfig.SkillEffectivenessEnabled defaults true, matching the tracker's own DI gate —
        // a fresh AppConfig() needs no explicit setup for the "enabled" cases below.
        _classifier
            .Setup(c => c.ClassifyAsync(It.IsAny<AgentTurnContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RequestIntentAssessment
            {
                Intent = RequestIntent.CodeGeneration,
                Confidence = 0.9,
                Source = ClassificationSource.LlmClassifier
            });
    }

    [Fact]
    public async Task Handle_NonAgentTurnRequest_PassesThroughWithoutTracking()
    {
        var behavior = CreateBehavior<NonAgentRequest, string>();

        var result = await behavior.Handle(new NonAgentRequest(), () => Task.FromResult("passthrough"), CancellationToken.None);

        result.Should().Be("passthrough");
        _tracker.Verify(
            t => t.RecordOutcomeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_ConfigDisabled_SkipsTrackingAndClassification()
    {
        _appConfig.AI.Rag.GraphRag.SkillEffectivenessEnabled = false;
        var behavior = CreateAgentTurnBehavior();
        var response = CreateResult(success: true, skillIds: ["researcher"]);

        await behavior.Handle(CreateCommand("hi"), () => Task.FromResult(response), CancellationToken.None);

        // No poll needed: with the gate off, Handle never schedules the background task at all.
        _classifier.Verify(c => c.ClassifyAsync(It.IsAny<AgentTurnContext>(), It.IsAny<CancellationToken>()), Times.Never);
        _tracker.Verify(
            t => t.RecordOutcomeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_NoSkillIds_SkipsTrackingAndClassification()
    {
        // An empty SkillIds means "no attribution" (e.g. Magentic turns) — never guess by classifying
        // a turn that has no skill to attribute the classification to.
        var behavior = CreateAgentTurnBehavior();
        var response = CreateResult(success: true, skillIds: []);

        await behavior.Handle(CreateCommand("hi"), () => Task.FromResult(response), CancellationToken.None);

        _classifier.Verify(c => c.ClassifyAsync(It.IsAny<AgentTurnContext>(), It.IsAny<CancellationToken>()), Times.Never);
        _tracker.Verify(
            t => t.RecordOutcomeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_SuccessfulTurn_RecordsOutcomeForTheSkill()
    {
        var recorded = SetupCapture();
        var behavior = CreateAgentTurnBehavior();
        var response = CreateResult(success: true, skillIds: ["researcher"]);

        await behavior.Handle(CreateCommand("find prior art"), () => Task.FromResult(response), CancellationToken.None);

        await WaitForAsync(() => CountOf(recorded) == 1, RecordTimeout);
        var (skillId, classification, succeeded) = recorded.Single();
        skillId.Should().Be("researcher");
        classification.Should().Be(nameof(RequestIntent.CodeGeneration));
        succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_FailedTurn_RecordsFailureOutcome()
    {
        var recorded = SetupCapture();
        var behavior = CreateAgentTurnBehavior();
        var response = CreateResult(success: false, skillIds: ["researcher"]);

        await behavior.Handle(CreateCommand("find prior art"), () => Task.FromResult(response), CancellationToken.None);

        await WaitForAsync(() => CountOf(recorded) == 1, RecordTimeout);
        recorded.Single().Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_MultipleSkillIds_RecordsOnceEachWithTheSameClassification()
    {
        var recorded = SetupCapture();
        var behavior = CreateAgentTurnBehavior();
        var response = CreateResult(success: true, skillIds: ["researcher", "writer"]);

        await behavior.Handle(CreateCommand("research and write"), () => Task.FromResult(response), CancellationToken.None);

        await WaitForAsync(() => CountOf(recorded) == 2, RecordTimeout);
        recorded.Select(r => r.SkillId).Should().BeEquivalentTo(["researcher", "writer"]);
        recorded.Should().OnlyContain(r => r.Classification == nameof(RequestIntent.CodeGeneration));
    }

    [Fact]
    public async Task Handle_ClassifierThrows_NoOutcomeRecordedAndFailureAbsorbed()
    {
        _classifier
            .Setup(c => c.ClassifyAsync(It.IsAny<AgentTurnContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("router unavailable"));
        var behavior = CreateAgentTurnBehavior();
        var response = CreateResult(success: true, skillIds: ["researcher"]);

        var result = await behavior.Handle(CreateCommand("hi"), () => Task.FromResult(response), CancellationToken.None);

        result.Should().BeSameAs(response, "tracking is an enhancement, never a hard dependency of a turn");
        // No classification means no query-classification key to record under — give the background
        // task a moment to run and confirm it genuinely recorded nothing, not just "not yet."
        await Task.Delay(200);
        _tracker.Verify(
            t => t.RecordOutcomeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_OneSkillRecordThrows_OtherSkillStillRecorded()
    {
        var attemptedSkillIds = new List<string>();
        _tracker
            .Setup(t => t.RecordOutcomeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, bool, double?, CancellationToken>((skillId, _, _, _, _) =>
            {
                lock (attemptedSkillIds) { attemptedSkillIds.Add(skillId); }
            })
            .Returns((string skillId, string _, bool _, double? _, CancellationToken _) =>
                skillId == "flaky" ? Task.FromException(new InvalidOperationException("write failed")) : Task.CompletedTask);

        var behavior = CreateAgentTurnBehavior();
        var response = CreateResult(success: true, skillIds: ["flaky", "researcher"]);

        await behavior.Handle(CreateCommand("hi"), () => Task.FromResult(response), CancellationToken.None);

        await WaitForAsync(() => CountOf(attemptedSkillIds) == 2, RecordTimeout);
        attemptedSkillIds.Should().BeEquivalentTo(["flaky", "researcher"],
            "one skill's write failure must not abort recording the next skill");
    }

    // --- Helpers ---

    private sealed record RecordedOutcome(string SkillId, string Classification, bool Succeeded);

    private List<RecordedOutcome> SetupCapture()
    {
        var captured = new List<RecordedOutcome>();
        _tracker
            .Setup(t => t.RecordOutcomeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, bool, double?, CancellationToken>((skillId, classification, succeeded, _, _) =>
            {
                lock (captured) { captured.Add(new RecordedOutcome(skillId, classification, succeeded)); }
            })
            .Returns(Task.CompletedTask);
        return captured;
    }

    private static async Task WaitForAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(10);
        }
        throw new TimeoutException($"Predicate did not become true within {timeout.TotalMilliseconds}ms.");
    }

    private static int CountOf<T>(List<T> items)
    {
        lock (items)
        {
            return items.Count;
        }
    }

    private IServiceScopeFactory BuildScopeFactory()
    {
        var provider = new Mock<IServiceProvider>();
        provider.Setup(p => p.GetService(typeof(IRequestIntentClassifier))).Returns(_classifier.Object);
        provider.Setup(p => p.GetService(typeof(ISkillEffectivenessTracker))).Returns(_tracker.Object);

        var scope = new Mock<IServiceScope>();
        scope.SetupGet(s => s.ServiceProvider).Returns(provider.Object);

        var factory = new Mock<IServiceScopeFactory>();
        factory.Setup(f => f.CreateScope()).Returns(scope.Object);
        return factory.Object;
    }

    private static IAmbientRequestScope BuildAmbientScope()
    {
        var ambient = new Mock<IAmbientRequestScope>();
        ambient.Setup(a => a.BeginScope(It.IsAny<IServiceProvider>())).Returns(Mock.Of<IDisposable>());
        return ambient.Object;
    }

    private SkillEffectivenessTrackingBehavior<TRequest, TResponse> CreateBehavior<TRequest, TResponse>()
        where TRequest : notnull =>
        new(
            BuildScopeFactory(),
            BuildAmbientScope(),
            Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == _appConfig),
            NullLogger<SkillEffectivenessTrackingBehavior<TRequest, TResponse>>.Instance);

    private SkillEffectivenessTrackingBehavior<ExecuteAgentTurnCommand, AgentTurnResult> CreateAgentTurnBehavior() =>
        CreateBehavior<ExecuteAgentTurnCommand, AgentTurnResult>();

    private static ExecuteAgentTurnCommand CreateCommand(
        string userMessage, string conversationId = "conv-1", int turnNumber = 1) =>
        new()
        {
            AgentName = "test-agent",
            UserMessage = userMessage,
            ConversationId = conversationId,
            TurnNumber = turnNumber
        };

    private static AgentTurnResult CreateResult(bool success, IReadOnlyList<string> skillIds) =>
        new()
        {
            Success = success,
            Response = success ? "done" : string.Empty,
            UpdatedHistory = [],
            SkillIds = skillIds
        };

    private sealed record NonAgentRequest : IRequest<string>;
}
