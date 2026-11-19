using Application.AI.Common.Exceptions;
using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Models.Conversations;
using Application.AI.Common.Services;
using Application.Common.Exceptions.ExceptionTypes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Application.AI.Common.Tests.Services;

/// <summary>
/// <see cref="RejectedTurnMessage"/> (#959): which rejected turns lose their stored message, what the caller is told,
/// and that the removal happens even when the caller has gone.
/// </summary>
public sealed class RejectedTurnMessageTests
{
    private readonly Mock<IConversationStore> _store = new();

    public static TheoryData<Exception, bool> Rejections => new()
    {
        { new ContentSafetyException("blocked", "hate"), true },
        { new ContentSafetyUnavailableException(), true },
        { new PromptInjectionBlockedException("blocked"), true },
        { new ValidationException("UserMessage: too long"), true },
        { new InvalidOperationException("a fault, not a rejection"), false },
        { new TimeoutException(), false },
    };

    [Theory]
    [MemberData(nameof(Rejections))]
    public void IsRejection_RecognisesScreeningAndValidationOnly(Exception ex, bool expected) =>
        RejectedTurnMessage.IsRejection(ex).Should().Be(expected);

    public static TheoryData<Exception, bool, bool> Removals => new()
    {
        // Stored for this turn: never admitted, so removed whatever the rejection.
        { new ContentSafetyUnavailableException(), true, true },
        // A retried message during an outage: no verdict on text an earlier turn stored, so kept.
        { new ContentSafetyUnavailableException(), false, false },
        // A retried message that is a verdict now: removed, as the earlier turn may not have screened it.
        { new ContentSafetyException("blocked", "hate"), false, true },
        { new ValidationException("UserMessage: too long"), false, true },
    };

    [Theory]
    [MemberData(nameof(Removals))]
    public void ShouldRemove_KeepsOnlyARetriedMessageThatCouldNotBeScreened(
        Exception rejection, bool storedForThisTurn, bool removed) =>
        RejectedTurnMessage.ShouldRemove(rejection, storedForThisTurn).Should().Be(removed);

    [Fact]
    public async Task RejectAsync_AValidationRejection_SaysTheMessageWasNotAcceptedWithoutEchoingIt()
    {
        var told = (await Reject(new ValidationException("UserMessage: secret text"))).ClientMessage;

        told.Should().Be(RejectedTurnMessage.NotAcceptedMessage).And.NotContain("secret");
    }

    [Fact]
    public async Task RejectAsync_RemovesUncancellably_SoADisconnectAfterTheRejectionCannotKeepTheMessage()
    {
        var id = Guid.NewGuid();

        await Reject(new ContentSafetyException("blocked", "hate"), id);

        _store.Verify(s => s.TruncateFromMessageAsync("c1", "u1", id,
            It.Is<CancellationToken>(t => !t.CanBeCanceled)), Times.Once);
    }

    [Fact]
    public async Task RejectAsync_AStoreFailure_IsLoggedAndTheCallerIsStillTold()
    {
        _store.Setup(s => s.TruncateFromMessageAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("disk"));

        var rejected = await Reject(new ContentSafetyException("blocked", "hate"));

        rejected.ClientMessage.Should().Be(IScreeningRefusal.BlockedMessage);
        rejected.RemovedMessageId.Should().BeNull("the store still holds the message, so the client must keep showing it");
    }

    [Fact]
    public async Task RejectAsync_WhenTheMessageIsRemoved_ReportsItsId_SoTheClientCanDropIt()
    {
        // #1036: the client shows the message before the turn runs. Unless it is told the store dropped the message,
        // it keeps showing text the conversation no longer holds, and a later retry or edit keyed by it fails.
        var id = Guid.NewGuid();
        StoreTruncates();

        var rejected = await Reject(new ContentSafetyException("blocked", "hate"), id);

        rejected.RemovedMessageId.Should().Be(id);
    }

    [Fact]
    public async Task RejectAsync_ARetryDuringAnOutage_ReportsNothingRemoved()
    {
        StoreTruncates();

        var rejected = await RejectedTurnMessage.RejectAsync(
            _store.Object, "c1", "u1", Guid.NewGuid(), storedForThisTurn: false, new ContentSafetyUnavailableException(),
            NullLogger.Instance);

        rejected.RemovedMessageId.Should().BeNull("an outage keeps a retried message an earlier turn stored");
    }

    [Fact]
    public async Task RejectAsync_WhenTheConversationIsGone_ReportsNothingRemoved()
    {
        _store.Setup(s => s.TruncateFromMessageAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ConversationRecord?)null);

        var rejected = await Reject(new ContentSafetyException("blocked", "hate"));

        rejected.RemovedMessageId.Should().BeNull();
    }

    private void StoreTruncates() =>
        _store.Setup(s => s.TruncateFromMessageAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationRecord("c1", "agent", "u1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []));

    private Task<RejectedTurn> Reject(Exception rejection, Guid? messageId = null) =>
        RejectedTurnMessage.RejectAsync(
            _store.Object, "c1", "u1", messageId ?? Guid.NewGuid(), storedForThisTurn: true, rejection,
            NullLogger.Instance);
}
