using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Orchestration.Magentic;
using Application.AI.Common.Interfaces.Telemetry;
using Domain.AI.Governance;
using Domain.AI.Telemetry.Redaction;
using FluentAssertions;
using Infrastructure.AI.Orchestration.Magentic;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Agents.AI.Workflows.Specialized.Magentic;
using Moq;
using Xunit;

#pragma warning disable MAAIW001

namespace Infrastructure.AI.Tests.Orchestration.Magentic;

/// <summary>
/// The subscriber reports each Magentic event it already turns into a span to
/// <see cref="IMagenticProgressNotifier"/> too (#progress), so a live surface can show the manager's plan
/// and each coordination round as they happen rather than only a final answer.
/// </summary>
[Collection("MagenticTraceCollection")]
public sealed class MagenticSubscriberProgressTests
{
    private static ICompositeResponseSanitizer StripsZeroWidthCharacters()
    {
        var sanitizer = new Mock<ICompositeResponseSanitizer>();
        sanitizer
            .Setup(s => s.Sanitize(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns((string content, string? _) => SanitizationResult.Clean(content.Replace("​", string.Empty)));
        return sanitizer.Object;
    }

    private static IContentRedactionFilter RedactsTheSecretWord()
    {
        var filter = new Mock<IContentRedactionFilter>();
        filter
            .Setup(f => f.Redact(It.IsAny<string?>(), It.IsAny<IReadOnlyList<RedactionCategory>>()))
            .Returns((string? content, IReadOnlyList<RedactionCategory> _) => content?.Replace("hunter2", "[REDACTED]"));
        return filter.Object;
    }

    private static MagenticEventSubscriber Build(
        RecordingMagenticProgressNotifier notifier,
        out Mock<IMagenticPlanReviewBridge> bridge,
        ICompositeResponseSanitizer? sanitizer = null,
        IContentRedactionFilter? redactionFilter = null)
    {
        var subscriber = MagenticTestHelpers.BuildSubscriber(
            out bridge, out _, sanitizer: sanitizer ?? StripsZeroWidthCharacters(),
            redactionFilter: redactionFilter ?? RedactsTheSecretWord(), progress: notifier);
        var request = MagenticTestHelpers.BuildRequest();
        subscriber.StartWorkflow(request, request.Name!, request.WorkflowId!.Value);
        return subscriber;
    }

    [Fact]
    public async Task PlanCreated_ReportsVersionOneWithTheLedgerText()
    {
        var notifier = new RecordingMagenticProgressNotifier();
        var subscriber = Build(notifier, out _);

        await subscriber.ProcessEventAsync(new MagenticPlanCreatedEvent(MagenticTestHelpers.AsLedger("the plan")), default);

        notifier.Plans.Should().Equal([(1, "the plan")]);
    }

    [Fact]
    public async Task Replanned_ReportsTheNextVersion()
    {
        var notifier = new RecordingMagenticProgressNotifier();
        var subscriber = Build(notifier, out _);

        await subscriber.ProcessEventAsync(new MagenticPlanCreatedEvent(MagenticTestHelpers.AsLedger("first")), default);
        await subscriber.ProcessEventAsync(new MagenticReplannedEvent(MagenticTestHelpers.AsLedger("second")), default);

        notifier.Plans.Should().Equal([(1, "first"), (2, "second")]);
    }

    [Fact]
    public async Task ProgressLedger_ReportsEachRoundWithItsSpeakerInstructionAndJudgements()
    {
        var notifier = new RecordingMagenticProgressNotifier();
        var subscriber = Build(notifier, out _);
        var ledger = MagenticTestHelpers.BuildLedger(
            isRequestSatisfied: false, isInLoop: true, isProgressBeingMade: false,
            nextSpeaker: "researcher", instructionOrQuestion: "find the report");

        await subscriber.ProcessEventAsync(new MagenticProgressLedgerUpdatedEvent(ledger), default);
        await subscriber.ProcessEventAsync(new MagenticProgressLedgerUpdatedEvent(ledger), default);

        notifier.Rounds.Select(r => r.Round).Should().Equal(1, 2);
        notifier.Rounds[0].Should().Be(new MagenticRoundReport
        {
            Round = 1,
            NextSpeaker = "researcher",
            Instruction = "find the report",
            RequestSatisfied = false,
            InLoop = true,
            Progressing = false,
        });
    }

    [Fact]
    public async Task PlanReviewRequest_IsReportedBeforeTheBridgeIsAsked()
    {
        // The reviewer is a human who can only act on a plan they have been shown, and the bridge call can
        // block for minutes, so the report must land first.
        var notifier = new RecordingMagenticProgressNotifier();
        var subscriber = Build(notifier, out var bridge);
        var reportedWhenAsked = -1;
        bridge.Setup(b => b.RequestPlanReviewAsync(It.IsAny<MagenticPlanReviewInput>(), It.IsAny<CancellationToken>()))
            .Callback(() => reportedWhenAsked = notifier.Reviews.Count)
            .ReturnsAsync(new MagenticPlanReviewOutcome { Approved = true });

        var planReview = new MagenticPlanReviewRequest(
            MagenticTestHelpers.AsLedger("draft plan"),
            MagenticTestHelpers.BuildLedger(),
            IsStalled: true);
        var port = new RequestPortInfo(
            new TypeId(typeof(MagenticPlanReviewRequest)),
            new TypeId(typeof(MagenticPlanReviewResponse)),
            "magentic.plan_review");
        var evt = new RequestInfoEvent(
            new ExternalRequest(port, Guid.NewGuid().ToString(), new PortableValue(planReview)));

        await subscriber.ProcessEventAsync(evt, default);

        reportedWhenAsked.Should().Be(1);
        notifier.Reviews.Should().Equal([("draft plan", false, true)]);
    }

    [Fact]
    public async Task OutboundText_IsSanitizedBeforeItIsRedacted()
    {
        // A secret split by an invisible character defeats an anchored redaction pattern unless the
        // sanitizer canonicalizes it away first (#470). The plan and the instruction are both model-authored.
        var notifier = new RecordingMagenticProgressNotifier();
        var subscriber = Build(notifier, out _);
        var ledger = MagenticTestHelpers.BuildLedger(instructionOrQuestion: "use hun​ter2 to log in");

        await subscriber.ProcessEventAsync(
            new MagenticPlanCreatedEvent(MagenticTestHelpers.AsLedger("password is hun​ter2")), default);
        await subscriber.ProcessEventAsync(new MagenticProgressLedgerUpdatedEvent(ledger), default);

        notifier.Plans.Single().Text.Should().Be("password is [REDACTED]");
        notifier.Rounds.Single().Instruction.Should().Be("use [REDACTED] to log in");
    }

    [Fact]
    public async Task OutboundText_IsBounded()
    {
        var notifier = new RecordingMagenticProgressNotifier();
        var subscriber = Build(notifier, out _);

        await subscriber.ProcessEventAsync(
            new MagenticPlanCreatedEvent(MagenticTestHelpers.AsLedger(new string('x', 50_000))), default);

        notifier.Plans.Single().Text.Length.Should().BeLessThanOrEqualTo(MagenticEventSubscriber.MaxProgressTextLength);
    }

    [Fact]
    public async Task ANotifierThatThrows_DoesNotStopTheWorkflowsEvents()
    {
        var notifier = new RecordingMagenticProgressNotifier { ThrowOnEveryCall = new InvalidOperationException("sink down") };
        var subscriber = Build(notifier, out _);

        var act = async () =>
        {
            await subscriber.ProcessEventAsync(new MagenticPlanCreatedEvent(MagenticTestHelpers.AsLedger("plan")), default);
            await subscriber.ProcessEventAsync(
                new MagenticProgressLedgerUpdatedEvent(MagenticTestHelpers.BuildLedger()), default);
        };

        await act.Should().NotThrowAsync();
        subscriber.RoundsExecuted.Should().Be(1, "the workflow carried on after the notifier failed");
    }

    [Fact]
    public async Task WhenTheRunIsCancelled_ANotifierCancellationPropagates()
    {
        var notifier = new RecordingMagenticProgressNotifier { ThrowOnEveryCall = new OperationCanceledException() };
        var subscriber = Build(notifier, out _);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => subscriber.ProcessEventAsync(
            new MagenticPlanCreatedEvent(MagenticTestHelpers.AsLedger("plan")), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ANotifierTimeoutThatIsNotTheRunsCancellation_DoesNotFailTheWorkflow()
    {
        // A sink that times out internally throws a cancellation of its own (a TaskCanceledException from
        // a write timeout). That is the sink failing, not the run being cancelled, and must not kill the run.
        var notifier = new RecordingMagenticProgressNotifier { ThrowOnEveryCall = new TaskCanceledException("write timed out") };
        var subscriber = Build(notifier, out _);

        var act = () => subscriber.ProcessEventAsync(
            new MagenticPlanCreatedEvent(MagenticTestHelpers.AsLedger("plan")), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task NextSpeaker_IsSanitizedRedactedAndBoundedLikeTheOtherManagerText()
    {
        // The speaker is a field the manager model writes, so it is as untrusted as the plan and the
        // instruction; it is a name, so its bound is much smaller.
        var notifier = new RecordingMagenticProgressNotifier();
        var subscriber = Build(notifier, out _);
        var ledger = MagenticTestHelpers.BuildLedger(nextSpeaker: "hun\u200bter2" + new string('x', 5_000));

        await subscriber.ProcessEventAsync(new MagenticProgressLedgerUpdatedEvent(ledger), default);

        var speaker = notifier.Rounds.Single().NextSpeaker!;
        speaker.Should().StartWith("[REDACTED]");
        speaker.Length.Should().BeLessThanOrEqualTo(MagenticEventSubscriber.MaxSpeakerLength);
    }

    [Fact]
    public async Task APlanReviewIsGivenRoomForTheWholePlan_AndSaysWhenItWasStillCut()
    {
        // The reviewer is a human who can only approve what they have been shown. A review is held to a much
        // larger bound than a progress line, and when even that cuts the plan the event says so rather than
        // presenting a truncated plan as the whole.
        var notifier = new RecordingMagenticProgressNotifier();
        var subscriber = Build(notifier, out var bridge);
        bridge.Setup(b => b.RequestPlanReviewAsync(It.IsAny<MagenticPlanReviewInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MagenticPlanReviewOutcome { Approved = true });

        async Task ReviewAsync(string plan)
        {
            var review = new MagenticPlanReviewRequest(
                MagenticTestHelpers.AsLedger(plan), MagenticTestHelpers.BuildLedger(), IsStalled: false);
            var port = new RequestPortInfo(
                new TypeId(typeof(MagenticPlanReviewRequest)),
                new TypeId(typeof(MagenticPlanReviewResponse)),
                "magentic.plan_review");
            await subscriber.ProcessEventAsync(
                new RequestInfoEvent(new ExternalRequest(port, Guid.NewGuid().ToString(), new PortableValue(review))), default);
        }

        await ReviewAsync(new string('p', MagenticEventSubscriber.MaxProgressTextLength + 1_000));
        await ReviewAsync(new string('p', MagenticEventSubscriber.MaxReviewTextLength + 1_000));

        notifier.Reviews[0].Text.Length.Should().BeGreaterThan(MagenticEventSubscriber.MaxProgressTextLength);
        notifier.Reviews[0].Truncated.Should().BeFalse("it fit in the review bound");
        notifier.Reviews[1].Text.Length.Should().BeLessThanOrEqualTo(MagenticEventSubscriber.MaxReviewTextLength);
        notifier.Reviews[1].Truncated.Should().BeTrue();
    }

    [Fact]
    public async Task APlanReview_StillReachesTheBridge_WhenPreparingTheProgressTextFails()
    {
        // The progress line is a courtesy; the review is the control. A fault while sanitizing text for the
        // line must not be able to stop the human being asked.
        var throwing = new Mock<ICompositeResponseSanitizer>();
        throwing.Setup(x => x.Sanitize(It.IsAny<string>(), It.IsAny<string?>())).Throws(new InvalidOperationException("scanner fault"));
        var notifier = new RecordingMagenticProgressNotifier();
        var subscriber = Build(notifier, out var bridge, sanitizer: throwing.Object);
        bridge.Setup(b => b.RequestPlanReviewAsync(It.IsAny<MagenticPlanReviewInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MagenticPlanReviewOutcome { Approved = true });
        var review = new MagenticPlanReviewRequest(
            MagenticTestHelpers.AsLedger("plan"), MagenticTestHelpers.BuildLedger(), IsStalled: false);
        var port = new RequestPortInfo(
            new TypeId(typeof(MagenticPlanReviewRequest)),
            new TypeId(typeof(MagenticPlanReviewResponse)),
            "magentic.plan_review");

        var response = await subscriber.ProcessEventAsync(
            new RequestInfoEvent(new ExternalRequest(port, Guid.NewGuid().ToString(), new PortableValue(review))), default);

        response.Should().NotBeNull("the reviewer was still asked and their answer still relayed");
        bridge.Verify(b => b.RequestPlanReviewAsync(It.IsAny<MagenticPlanReviewInput>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ATerminalReportToAStalledSink_IsGivenUpOn_SoTheRunCanFinish()
    {
        // Terminal reports go out on a token the cancelled run cannot cancel, so they carry their own
        // bound: a half-open client must not hold a finished or cancelled run open forever.
        var notifier = new RecordingMagenticProgressNotifier { BlockTerminalCallsUntilCancelled = true };
        var subscriber = MagenticTestHelpers.BuildSubscriber(
            out _, out _, sanitizer: StripsZeroWidthCharacters(), redactionFilter: RedactsTheSecretWord(),
            progress: notifier, terminalReportTimeout: TimeSpan.FromMilliseconds(100));
        var request = MagenticTestHelpers.BuildRequest();
        subscriber.StartWorkflow(request, request.Name!, request.WorkflowId!.Value);

        var act = () => subscriber.NotifyFailedAsync("magentic.cancelled", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        await act.Should().NotThrowAsync();
        notifier.Failed.Should().Equal(["magentic.cancelled"]);
    }

    [Fact]
    public async Task LifecycleNotifications_ReportStartedCompletedAndFailed()
    {
        var notifier = new RecordingMagenticProgressNotifier();
        var subscriber = Build(notifier, out _);
        await subscriber.ProcessEventAsync(
            new MagenticProgressLedgerUpdatedEvent(MagenticTestHelpers.BuildLedger()), default);

        await subscriber.NotifyStartedAsync(default);
        await subscriber.NotifyCompletedAsync("satisfied", default);
        await subscriber.NotifyFailedAsync("magentic.cancelled", default);

        notifier.Started.Single().Participants.Should().NotBeEmpty();
        notifier.Completed.Should().Equal([("satisfied", 1)]);
        notifier.Failed.Should().Equal(["magentic.cancelled"]);
    }
}

#pragma warning restore MAAIW001
