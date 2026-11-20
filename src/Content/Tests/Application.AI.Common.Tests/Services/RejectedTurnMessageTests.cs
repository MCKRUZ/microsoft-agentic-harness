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
/// <see cref="RejectedTurnMessage"/> (#959, #964): which exceptions are rejections, what the caller is told, and which
/// rejections of a retried, already stored message remove it.
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

    public static TheoryData<Exception, bool> RetryRemovals => new()
    {
        // An outage is no verdict on text an earlier turn stored and admitted, so it stays.
        { new ContentSafetyUnavailableException(), false },
        // A verdict now (detection since tightened) means it should not stay in the history either.
        { new ContentSafetyException("blocked", "hate"), true },
        { new PromptInjectionBlockedException("blocked"), true },
        { new ValidationException("UserMessage: too long"), true },
    };

    [Theory]
    [MemberData(nameof(RetryRemovals))]
    public void RemovesRetriedMessage_KeepsItOnlyThroughAnOutage(Exception rejection, bool removed) =>
        RejectedTurnMessage.RemovesRetriedMessage(rejection).Should().Be(removed);

    [Fact]
    public void ClientMessageFor_AValidationRejection_SaysTheMessageWasNotAcceptedWithoutEchoingIt() =>
        RejectedTurnMessage.ClientMessageFor(new ValidationException("UserMessage: secret text"))
            .Should().Be(RejectedTurnMessage.NotAcceptedMessage).And.NotContain("secret");

    [Fact]
    public void ClientMessageFor_AScreeningRefusal_UsesTheRefusalsOwnMessage() =>
        RejectedTurnMessage.ClientMessageFor(new ContentSafetyUnavailableException())
            .Should().Be(new ContentSafetyUnavailableException().ClientMessage);

    [Fact]
    public async Task RemoveRetriedMessageAsync_RemovesUncancellably_SoADisconnectCannotKeepTheMessage()
    {
        var id = Guid.NewGuid();

        await RejectedTurnMessage.RemoveRetriedMessageAsync(_store.Object, "c1", "u1", id, NullLogger.Instance);

        _store.Verify(s => s.TruncateFromMessageAsync("c1", "u1", id,
            It.Is<CancellationToken>(t => !t.CanBeCanceled)), Times.Once);
    }

    [Fact]
    public async Task RemoveRetriedMessageAsync_AStoreFailure_IsLoggedAndReportsNothingRemoved()
    {
        _store.Setup(s => s.TruncateFromMessageAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("disk"));

        var remaining = await RejectedTurnMessage.RemoveRetriedMessageAsync(
            _store.Object, "c1", "u1", Guid.NewGuid(), NullLogger.Instance);

        remaining.Should().BeNull("the store still holds the message, so the client must keep showing it");
    }

    [Fact]
    public async Task RemoveRetriedMessageAsync_ReturnsWhatIsLeft()
    {
        var left = new ConversationRecord("c1", "agent", "u1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []);
        _store.Setup(s => s.TruncateFromMessageAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(left);

        var remaining = await RejectedTurnMessage.RemoveRetriedMessageAsync(
            _store.Object, "c1", "u1", Guid.NewGuid(), NullLogger.Instance);

        remaining.Should().BeSameAs(left);
    }
}
