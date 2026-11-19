using Application.AI.Common.Exceptions;
using Application.AI.Common.Interfaces.AI;
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
        var told = await Reject(new ValidationException("UserMessage: secret text"));

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

        var told = await Reject(new ContentSafetyException("blocked", "hate"));

        told.Should().Be(IScreeningRefusal.BlockedMessage);
    }

    private Task<string> Reject(Exception rejection, Guid? messageId = null) =>
        RejectedTurnMessage.RejectAsync(
            _store.Object, "c1", "u1", messageId ?? Guid.NewGuid(), storedForThisTurn: true, rejection,
            NullLogger.Instance);
}
