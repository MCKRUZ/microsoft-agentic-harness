using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Models.Conversations;
using Application.AI.Common.Services;
using FluentAssertions;
using Moq;

namespace Application.AI.Common.Tests.Services;

/// <summary>
/// <see cref="ConversationDispatchHistory"/> (#785): the history a turn is dispatched with excludes the
/// message being sent, which the interactive transports have already stored.
/// </summary>
public sealed class ConversationDispatchHistoryTests
{
    private readonly Mock<IConversationStore> _store = new();

    private static ConversationMessage Msg(MessageRole role, string text) =>
        new(Guid.NewGuid(), role, text, DateTimeOffset.UtcNow);

    /// <summary>A store that answers like the real one: the last N messages of the transcript.</summary>
    private void Transcript(params ConversationMessage[] transcript) =>
        _store
            .Setup(s => s.GetHistoryForDispatch("c1", "u1", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string _, int n, CancellationToken _) =>
                (IReadOnlyList<ConversationMessage>?)transcript.TakeLast(Math.Max(0, n)).ToList());

    private Task<IReadOnlyList<ConversationMessage>> Read(int max, string userMessage) =>
        ConversationDispatchHistory.ReadPriorToAsync(_store.Object, "c1", "u1", max, userMessage, CancellationToken.None);

    [Fact]
    public async Task ReadPriorToAsync_WindowEndsWithTheMessageBeingSent_ExcludesIt()
    {
        Transcript(Msg(MessageRole.User, "a"), Msg(MessageRole.Assistant, "b"), Msg(MessageRole.User, "c"));

        var prior = await Read(10, "c");

        prior.Select(m => m.Content).Should().Equal("a", "b");
    }

    [Fact]
    public async Task ReadPriorToAsync_ReturnsTheFullRequestedNumberOfPriorMessages()
    {
        // Asking for 3 prior messages must not return 2 because the in-flight one used a slot.
        Transcript(Msg(MessageRole.Assistant, "1"), Msg(MessageRole.Assistant, "2"), Msg(MessageRole.Assistant, "3"),
            Msg(MessageRole.Assistant, "4"), Msg(MessageRole.User, "now"));

        var prior = await Read(3, "now");

        prior.Select(m => m.Content).Should().Equal("2", "3", "4");
    }

    [Fact]
    public async Task ReadPriorToAsync_LastMessageIsNotAUserMessage_RemovesNothing()
    {
        // Only the in-flight message is excluded; an assistant reply with the same text is history.
        Transcript(Msg(MessageRole.User, "a"), Msg(MessageRole.Assistant, "c"));

        var prior = await Read(10, "c");

        prior.Select(m => m.Content).Should().Equal("a", "c");
    }

    [Fact]
    public async Task ReadPriorToAsync_LastUserMessageHasDifferentContent_RemovesNothing()
    {
        // If the store ever answers a window that does not end with the message being sent, dropping
        // the last message would silently discard real history.
        Transcript(Msg(MessageRole.Assistant, "a"), Msg(MessageRole.User, "something else"));

        var prior = await Read(10, "c");

        prior.Select(m => m.Content).Should().Equal("a", "something else");
    }

    [Fact]
    public async Task ReadPriorToAsync_NeverReturnsMoreThanRequestedEvenWhenNothingMatched()
    {
        Transcript(Msg(MessageRole.Assistant, "1"), Msg(MessageRole.Assistant, "2"), Msg(MessageRole.Assistant, "3"));

        var prior = await Read(2, "not-in-the-window");

        prior.Select(m => m.Content).Should().Equal("2", "3");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ReadPriorToAsync_NonPositiveWindow_ReturnsNothing_NotTheWholeTranscript(int max)
    {
        // The store defines a non-positive window as none; asking for one more than that must not turn
        // zero into "the in-flight message", or minus one into something unbounded.
        Transcript(Msg(MessageRole.User, "a"), Msg(MessageRole.Assistant, "b"), Msg(MessageRole.User, "c"));

        var prior = await Read(max, "c");

        prior.Should().BeEmpty();
        _store.Verify(s => s.GetHistoryForDispatch("c1", "u1", 0, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReadPriorToAsync_ConversationDoesNotExist_ReturnsEmpty()
    {
        _store
            .Setup(s => s.GetHistoryForDispatch("c1", "u1", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ConversationMessage>?)null);

        (await Read(10, "c")).Should().BeEmpty();
    }
}
