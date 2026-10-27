using Application.AI.Common.Exceptions;
using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Services;
using Application.Common.Exceptions.ExceptionTypes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Application.AI.Common.Tests.Services;

/// <summary>
/// <see cref="RejectedTurnMessage"/> (#959): which rejected turns lose their stored message, and that the removal
/// happens even when the caller has gone.
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

    [Fact]
    public void ShouldRemove_AMessageStoredForThisTurn_IsAlwaysRemoved()
    {
        RejectedTurnMessage.ShouldRemove(new ContentSafetyUnavailableException(), storedForThisTurn: true)
            .Should().BeTrue("it was never admitted, outage or not");
    }

    [Fact]
    public void ShouldRemove_ARetriedMessageDuringAnOutage_IsKept()
    {
        RejectedTurnMessage.ShouldRemove(new ContentSafetyUnavailableException(), storedForThisTurn: false)
            .Should().BeFalse("an outage is no verdict on text an earlier turn stored");
    }

    [Fact]
    public void ShouldRemove_ARetriedMessageDefinitelyRejected_IsRemoved()
    {
        RejectedTurnMessage.ShouldRemove(new ContentSafetyException("blocked", "hate"), storedForThisTurn: false)
            .Should().BeTrue();
    }

    [Fact]
    public void ClientMessage_AValidationRejection_SaysTheMessageWasNotAcceptedWithoutEchoingIt()
    {
        RejectedTurnMessage.ClientMessage(new ValidationException("UserMessage: secret text"))
            .Should().Be(RejectedTurnMessage.NotAcceptedMessage).And.NotContain("secret");
    }

    [Fact]
    public async Task RemoveAsync_RunsUncancellably_SoADisconnectAfterTheRejectionCannotKeepTheMessage()
    {
        var id = Guid.NewGuid();

        await RejectedTurnMessage.RemoveAsync(_store.Object, "c1", "u1", id, NullLogger.Instance);

        _store.Verify(s => s.TruncateFromMessageAsync("c1", "u1", id,
            It.Is<CancellationToken>(t => !t.CanBeCanceled)), Times.Once);
    }

    [Fact]
    public async Task RemoveAsync_AStoreFailure_IsLoggedNotThrown()
    {
        _store.Setup(s => s.TruncateFromMessageAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("disk"));

        var act = () => RejectedTurnMessage.RemoveAsync(_store.Object, "c1", "u1", Guid.NewGuid(), NullLogger.Instance);

        await act.Should().NotThrowAsync("the caller is still told the turn was rejected");
    }
}
