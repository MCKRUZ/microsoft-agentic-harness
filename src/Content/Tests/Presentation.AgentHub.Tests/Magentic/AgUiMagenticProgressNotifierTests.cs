using FluentAssertions;
using Application.AI.Common.Interfaces.Orchestration.Magentic;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Presentation.AgentHub.AgUi;
using Presentation.AgentHub.Magentic;
using Xunit;

namespace Presentation.AgentHub.Tests.Magentic;

/// <summary>
/// <see cref="AgUiMagenticProgressNotifier"/> translates Magentic workflow progress into AG-UI events on
/// the active run's stream, and never lets the stream take the workflow down.
/// </summary>
public sealed class AgUiMagenticProgressNotifierTests
{
    private static readonly Guid WorkflowId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly Mock<IAgUiEventWriterAccessor> _accessor = new();
    private readonly Mock<IAgUiEventWriter> _writer = new();
    private readonly List<AgUiEvent> _written = [];
    private readonly AgUiMagenticProgressNotifier _sut;

    public AgUiMagenticProgressNotifierTests()
    {
        _accessor.Setup(a => a.Writer).Returns(_writer.Object);
        _writer
            .Setup(w => w.WriteAsync(It.IsAny<AgUiEvent>(), It.IsAny<CancellationToken>()))
            .Callback<AgUiEvent, CancellationToken>((e, _) => _written.Add(e))
            .Returns(Task.CompletedTask);
        _sut = new AgUiMagenticProgressNotifier(_accessor.Object, NullLogger<AgUiMagenticProgressNotifier>.Instance);
    }

    [Fact]
    public async Task WorkflowStarted_EmitsTheStartedEvent()
    {
        await _sut.NotifyWorkflowStartedAsync(WorkflowId, "team", ["a", "b"], CancellationToken.None);

        _written.Should().ContainSingle().Which.Should().BeEquivalentTo(new MagenticWorkflowStartedEvent
        {
            WorkflowId = WorkflowId.ToString(),
            WorkflowName = "team",
            Participants = ["a", "b"],
        });
    }

    [Fact]
    public async Task Plan_EmitsThePlanEvent()
    {
        await _sut.NotifyPlanAsync(WorkflowId, 2, "the plan", CancellationToken.None);

        _written.Should().ContainSingle().Which.Should().BeEquivalentTo(new MagenticPlanEvent
        {
            WorkflowId = WorkflowId.ToString(),
            PlanVersion = 2,
            PlanText = "the plan",
        });
    }

    [Fact]
    public async Task Round_EmitsTheRoundEvent()
    {
        await _sut.NotifyRoundAsync(
            WorkflowId,
            new MagenticRoundReport
            {
                Round = 3,
                NextSpeaker = "researcher",
                Instruction = "dig",
                RequestSatisfied = false,
                InLoop = true,
                Progressing = false,
            },
            CancellationToken.None);

        _written.Should().ContainSingle().Which.Should().BeEquivalentTo(new MagenticRoundEvent
        {
            WorkflowId = WorkflowId.ToString(),
            Round = 3,
            NextSpeaker = "researcher",
            Instruction = "dig",
            RequestSatisfied = false,
            InLoop = true,
            Progressing = false,
        });
    }

    [Fact]
    public async Task PlanReviewRequested_EmitsTheReviewEvent()
    {
        await _sut.NotifyPlanReviewRequestedAsync(WorkflowId, "draft", false, true, CancellationToken.None);

        _written.Should().ContainSingle().Which.Should().BeEquivalentTo(new MagenticPlanReviewRequestedEvent
        {
            WorkflowId = WorkflowId.ToString(),
            PlanText = "draft",
            PlanTruncated = false,
            IsStalled = true,
        });
    }

    [Fact]
    public async Task WorkflowCompleted_EmitsTheCompletedEvent()
    {
        await _sut.NotifyWorkflowCompletedAsync(WorkflowId, "satisfied", 4, CancellationToken.None);

        _written.Should().ContainSingle().Which.Should().BeEquivalentTo(new MagenticWorkflowCompletedEvent
        {
            WorkflowId = WorkflowId.ToString(),
            CompletionReason = "satisfied",
            RoundsExecuted = 4,
        });
    }

    [Fact]
    public async Task WorkflowFailed_EmitsTheFailedEvent()
    {
        await _sut.NotifyWorkflowFailedAsync(WorkflowId, "magentic.cancelled", CancellationToken.None);

        _written.Should().ContainSingle().Which.Should().BeEquivalentTo(new MagenticWorkflowFailedEvent
        {
            WorkflowId = WorkflowId.ToString(),
            ErrorCode = "magentic.cancelled",
        });
    }

    [Fact]
    public async Task Text_IsCappedEvenIfTheProducerDidNot()
    {
        // The producer bounds what it reports, but a second producer of this interface might not; the
        // frame that reaches a browser is bounded here regardless.
        await _sut.NotifyPlanAsync(WorkflowId, 1, new string('x', 100_000), CancellationToken.None);

        ((MagenticPlanEvent)_written.Single()).PlanText.Length
            .Should().BeLessThanOrEqualTo(AgUiMagenticProgressNotifier.MaxTextLength);
    }

    [Fact]
    public async Task PlanReviewText_GetsTheLargerBound_AndCutsAreFlagged()
    {
        // A plan under review is the one thing a human decides on, so it is held to a larger bound than a
        // progress line - and if even that cuts it the event says so.
        await _sut.NotifyPlanReviewRequestedAsync(
            WorkflowId, new string('p', AgUiMagenticProgressNotifier.MaxTextLength + 500), false, false, CancellationToken.None);
        await _sut.NotifyPlanReviewRequestedAsync(
            WorkflowId, new string('p', AgUiMagenticProgressNotifier.MaxReviewTextLength + 500), false, false, CancellationToken.None);

        var whole = (MagenticPlanReviewRequestedEvent)_written[0];
        var cut = (MagenticPlanReviewRequestedEvent)_written[1];
        whole.PlanText.Length.Should().BeGreaterThan(AgUiMagenticProgressNotifier.MaxTextLength);
        whole.PlanTruncated.Should().BeFalse();
        cut.PlanText.Length.Should().BeLessThanOrEqualTo(AgUiMagenticProgressNotifier.MaxReviewTextLength);
        cut.PlanTruncated.Should().BeTrue();
    }

    [Fact]
    public async Task APlanTheProducerAlreadyCut_StaysFlaggedAsCut()
    {
        await _sut.NotifyPlanReviewRequestedAsync(WorkflowId, "short", planTruncated: true, false, CancellationToken.None);

        ((MagenticPlanReviewRequestedEvent)_written.Single()).PlanTruncated.Should().BeTrue();
    }

    [Fact]
    public async Task WithNoActiveRun_ItSkipsQuietly()
    {
        _accessor.Setup(a => a.Writer).Returns((IAgUiEventWriter?)null);

        var act = () => _sut.NotifyPlanAsync(WorkflowId, 1, "plan", CancellationToken.None);

        await act.Should().NotThrowAsync();
        _written.Should().BeEmpty();
    }

    [Fact]
    public async Task AWriterThatFails_IsContained()
    {
        _writer
            .Setup(w => w.WriteAsync(It.IsAny<AgUiEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("client went away"));

        var act = () => _sut.NotifyPlanAsync(WorkflowId, 1, "plan", CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task WhenTheRunIsCancelled_AFailedWritePropagates()
    {
        _writer
            .Setup(w => w.WriteAsync(It.IsAny<AgUiEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => _sut.NotifyPlanAsync(WorkflowId, 1, "plan", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task AWriteTimeoutThatIsNotTheRunsCancellation_IsContained()
    {
        _writer
            .Setup(w => w.WriteAsync(It.IsAny<AgUiEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException("write timed out"));

        var act = () => _sut.NotifyPlanAsync(WorkflowId, 1, "plan", CancellationToken.None);

        await act.Should().NotThrowAsync();
    }
}
