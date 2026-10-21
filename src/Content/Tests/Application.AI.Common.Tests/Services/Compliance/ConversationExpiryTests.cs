using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Interfaces.Compliance;
using Application.AI.Common.Models.Conversations;
using Application.AI.Common.Services.Compliance;
using Domain.Common.Compliance;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace Application.AI.Common.Tests.Services.Compliance;

/// <summary>
/// Conversation expiry (#901): an idle conversation goes with everything found through it, in erasure order, and nothing
/// else of the person's goes with it; a conversation in use, or one that fails partway, is kept.
/// </summary>
public sealed class ConversationExpiryTests
{
    private static readonly TimeSpan Year = TimeSpan.FromDays(365);
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.Zero));
    private readonly Mock<IIdleConversationSource> _source = new();
    private readonly Mock<IConversationTurnLease> _lease = new();
    private readonly List<string> _calls = [];
    private readonly IdleConversation _idle;

    public ConversationExpiryTests()
    {
        _idle = new IdleConversation("c-1", "Owner-1", _time.GetUtcNow() - Year - TimeSpan.FromDays(1));
        _source.Setup(s => s.ListIdleAsync(It.IsAny<DateTimeOffset>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([_idle]);
        _source.Setup(s => s.IsIdleAsync(It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var handle = new Mock<IConversationTurnLeaseHandle>();
        handle.Setup(h => h.DisposeAsync()).Returns(() =>
        {
            _calls.Add("release");
            return ValueTask.CompletedTask;
        });
        _lease.Setup(l => l.AcquireAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((id, _) =>
            {
                _calls.Add($"lease {id}");
                return Task.FromResult(handle.Object);
            });
    }

    private Mock<IPersonalDataEraser> EraserMock(
        string name, ErasureStage stage, ErasureReach reach = ErasureReach.NamedConversations, Exception? failure = null)
    {
        var eraser = new Mock<IPersonalDataEraser>();
        eraser.SetupGet(e => e.ErasureStoreName).Returns(name);
        eraser.SetupGet(e => e.ErasureStage).Returns(stage);
        eraser.SetupGet(e => e.ErasureReach).Returns(reach);
        eraser.Setup(e => e.EraseSubjectAsync(It.IsAny<ErasureSubject>(), It.IsAny<CancellationToken>()))
            .Returns<ErasureSubject, CancellationToken>((s, _) =>
            {
                _calls.Add($"{name} {s.OwnerId} [{string.Join(",", s.ConversationIds)}]");
                return failure is null ? Task.FromResult(1) : Task.FromException<int>(failure);
            });
        return eraser;
    }

    private IPersonalDataEraser Eraser(
        string name, ErasureStage stage, ErasureReach reach = ErasureReach.NamedConversations, Exception? failure = null) =>
        EraserMock(name, stage, reach, failure).Object;

    private ConversationExpiry Create(params IPersonalDataEraser[] erasers) =>
        new(erasers, [_source.Object], _lease.Object, _time, NullLogger<ConversationExpiry>.Instance);

    private IPersonalDataEraser[] TheUsualStores() =>
    [
        Eraser("ledger", ErasureStage.AfterConversations),
        Eraser("conversations", ErasureStage.Conversations),
        Eraser("spills", ErasureStage.BeforeConversations),
    ];

    [Fact]
    public async Task AnIdleConversation_GoesWithWhatIsFoundThroughIt_InErasureOrder_UnderItsLease()
    {
        var expired = await Create(TheUsualStores()).ExpireIdleAsync(Year, 10, CancellationToken.None);

        expired.Should().Be(1);
        _calls.Should().Equal(
            "lease c-1",
            "spills Owner-1 [c-1]",
            "conversations Owner-1 [c-1]",
            "ledger Owner-1 [c-1]",
            "release");
    }

    [Fact]
    public async Task OwnerWideStores_AreNeverAsked_SoTheRestOfThePersonsDataStays()
    {
        var documents = EraserMock("documents", ErasureStage.BeforeConversations, ErasureReach.Owner);
        var schedules = EraserMock("schedules", ErasureStage.BeforeConversations, ErasureReach.Owner);

        await Create([.. TheUsualStores(), documents.Object, schedules.Object]).ExpireIdleAsync(Year, 10, CancellationToken.None);

        documents.Verify(e => e.EraseSubjectAsync(It.IsAny<ErasureSubject>(), It.IsAny<CancellationToken>()), Times.Never);
        schedules.Verify(e => e.EraseSubjectAsync(It.IsAny<ErasureSubject>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TheCutoff_IsTheIdlePeriodBeforeNow_AndTheBatchLimitIsPassedThrough()
    {
        await Create(TheUsualStores()).ExpireIdleAsync(Year, 7, CancellationToken.None);

        var cutoff = _time.GetUtcNow() - Year;
        _source.Verify(s => s.ListIdleAsync(cutoff, 7, It.IsAny<CancellationToken>()));
        _source.Verify(s => s.IsIdleAsync("c-1", cutoff, It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task AConversationUsedSinceItWasListed_IsKept()
    {
        _source.Setup(s => s.IsIdleAsync("c-1", It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var expired = await Create(TheUsualStores()).ExpireIdleAsync(Year, 10, CancellationToken.None);

        expired.Should().Be(0);
        _calls.Should().Equal("lease c-1", "release");
    }

    [Fact]
    public async Task AConversationWithATurnRunning_IsSkipped_AfterAShortWait()
    {
        _lease.Setup(l => l.AcquireAsync("c-1", It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>(async (_, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                throw new InvalidOperationException("unreachable");
            });

        var sweep = Create(TheUsualStores()).ExpireIdleAsync(Year, 10, CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(5));

        (await sweep).Should().Be(0);
        _calls.Should().BeEmpty("nothing may be deleted while a turn holds the conversation");
    }

    [Fact]
    public async Task AConversationAlreadyGone_IsSkipped()
    {
        _lease.Setup(l => l.AcquireAsync("c-1", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Conversation 'c-1' does not exist"));

        var expired = await Create(TheUsualStores()).ExpireIdleAsync(Year, 10, CancellationToken.None);

        expired.Should().Be(0);
        _calls.Should().BeEmpty();
    }

    [Fact]
    public async Task AStoreFailingBeforeTheConversation_KeepsTheConversation_SoTheNextSweepFindsItsRecordsAgain()
    {
        var expired = await Create(
                Eraser("spills", ErasureStage.BeforeConversations, failure: new IOException()),
                Eraser("conversations", ErasureStage.Conversations),
                Eraser("ledger", ErasureStage.AfterConversations))
            .ExpireIdleAsync(Year, 10, CancellationToken.None);

        expired.Should().Be(0);
        _calls.Should().Equal("lease c-1", "spills Owner-1 [c-1]", "release");
    }

    [Fact]
    public async Task TheConversationStoreFailing_KeepsTheLedger_SoNoOnceOnlyToolIsReArmed()
    {
        var expired = await Create(
                Eraser("conversations", ErasureStage.Conversations, failure: new IOException()),
                Eraser("ledger", ErasureStage.AfterConversations))
            .ExpireIdleAsync(Year, 10, CancellationToken.None);

        expired.Should().Be(0);
        _calls.Should().NotContain(c => c.StartsWith("ledger", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AStoreFailingAfterTheConversationIsDeleted_StillCountsItAsExpired()
    {
        var expired = await Create(
                Eraser("conversations", ErasureStage.Conversations),
                Eraser("ledger", ErasureStage.AfterConversations, failure: new IOException()))
            .ExpireIdleAsync(Year, 10, CancellationToken.None);

        expired.Should().Be(1, "the conversation is gone; claiming otherwise would hide that it was deleted");
    }

    [Fact]
    public async Task NoStoreThatDeletesTheConversation_NothingIsTouched()
    {
        // Emptying what is found through a conversation and leaving the conversation standing helps nobody.
        var expired = await Create(
                Eraser("spills", ErasureStage.BeforeConversations),
                Eraser("conversations", ErasureStage.Conversations, ErasureReach.Owner))
            .ExpireIdleAsync(Year, 10, CancellationToken.None);

        expired.Should().Be(0);
        _calls.Should().BeEmpty();
    }

    [Fact]
    public async Task AConversationStoreThatCannotListIdleConversations_NothingIsTouched()
    {
        var expiry = new ConversationExpiry(
            TheUsualStores(), [null], _lease.Object, _time, NullLogger<ConversationExpiry>.Instance);

        (await expiry.ExpireIdleAsync(Year, 10, CancellationToken.None)).Should().Be(0);
        _calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(60)]
    [InlineData(86_399)]
    public async Task AnIdlePeriodUnderADay_IsRefused_BeforeAnythingIsRead(int seconds)
    {
        var act = () => Create(TheUsualStores()).ExpireIdleAsync(TimeSpan.FromSeconds(seconds), 10, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        _source.Verify(s => s.ListIdleAsync(It.IsAny<DateTimeOffset>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExactlyOneDay_IsAccepted()
    {
        (await Create(TheUsualStores()).ExpireIdleAsync(TimeSpan.FromDays(1), 10, CancellationToken.None)).Should().Be(1);
    }

    [Fact]
    public async Task DeletedBytesAreCleared_AfterTheSweep_EvenWhenNothingExpired()
    {
        _source.Setup(s => s.ListIdleAsync(It.IsAny<DateTimeOffset>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var conversations = EraserMock("conversations", ErasureStage.Conversations);
        conversations.As<IPersonalDataScrubber>().Setup(s => s.ScrubAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                _calls.Add("scrub conversations");
                return Task.CompletedTask;
            });

        await Create(conversations.Object).ExpireIdleAsync(Year, 10, CancellationToken.None);

        _calls.Should().Equal("scrub conversations");
    }

    [Fact]
    public async Task OneConversationFailing_DoesNotStopTheNext()
    {
        var second = new IdleConversation("c-2", "Owner-2", _idle.LastActivityAt);
        _source.Setup(s => s.ListIdleAsync(It.IsAny<DateTimeOffset>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([_idle, second]);
        var conversations = EraserMock("conversations", ErasureStage.Conversations);
        conversations.Setup(e => e.EraseSubjectAsync(
                It.Is<ErasureSubject>(s => s.ConversationIds[0] == "c-1"), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException());

        var expired = await Create(conversations.Object).ExpireIdleAsync(Year, 10, CancellationToken.None);

        expired.Should().Be(1);
        conversations.Verify(e => e.EraseSubjectAsync(
            It.Is<ErasureSubject>(s => s.OwnerId == "Owner-2" && s.ConversationIds.SequenceEqual(new[] { "c-2" })),
            It.IsAny<CancellationToken>()));
    }
}
