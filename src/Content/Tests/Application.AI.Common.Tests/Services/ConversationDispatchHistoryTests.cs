using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Models.Conversations;
using Application.AI.Common.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace Application.AI.Common.Tests.Services;

/// <summary>
/// <see cref="ConversationDispatchHistory"/> (#785): the history a turn is dispatched with excludes the
/// message being sent, which the interactive transports have already stored.
/// </summary>
public sealed class ConversationDispatchHistoryTests
{
    private readonly Mock<IConversationStore> _store = new();
    private readonly CapturingLogger _logger = new();

    private static ConversationMessage Msg(MessageRole role, string text, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), role, text, DateTimeOffset.UtcNow);

    /// <summary>A store that answers like the real one: the last N messages of the transcript.</summary>
    private void Transcript(params ConversationMessage[] transcript) =>
        _store
            .Setup(s => s.GetHistoryForDispatch("c1", "u1", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string _, int n, CancellationToken _) =>
                (IReadOnlyList<ConversationMessage>?)transcript.TakeLast(Math.Max(0, n)).ToList());

    private Task<IReadOnlyList<ConversationMessage>> Read(int max, Guid inFlight) =>
        ConversationDispatchHistory.ReadPriorToAsync(
            _store.Object, "c1", "u1", max, inFlight, _logger, CancellationToken.None);

    [Fact]
    public async Task ReadPriorToAsync_WindowEndsWithTheMessageBeingSent_ExcludesIt()
    {
        var sending = Msg(MessageRole.User, "c");
        Transcript(Msg(MessageRole.User, "a"), Msg(MessageRole.Assistant, "b"), sending);

        var prior = await Read(10, sending.Id);

        prior.Select(m => m.Content).Should().Equal("a", "b");
        _logger.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadPriorToAsync_MatchesByIdNotByContent()
    {
        // A store that normalises text on write must not bring the duplicate back: the id is what the
        // caller stored the message under.
        var sending = Msg(MessageRole.User, "stored text differs from what was sent");
        Transcript(Msg(MessageRole.Assistant, "b"), sending);

        var prior = await Read(10, sending.Id);

        prior.Select(m => m.Content).Should().Equal("b");
    }

    [Fact]
    public async Task ReadPriorToAsync_ReturnsTheFullRequestedNumberOfPriorMessages()
    {
        // Asking for 3 prior messages must not return 2 because the in-flight one used a slot.
        var sending = Msg(MessageRole.User, "now");
        Transcript(Msg(MessageRole.Assistant, "1"), Msg(MessageRole.Assistant, "2"), Msg(MessageRole.Assistant, "3"),
            Msg(MessageRole.Assistant, "4"), sending);

        var prior = await Read(3, sending.Id);

        prior.Select(m => m.Content).Should().Equal("2", "3", "4");
    }

    [Fact]
    public async Task ReadPriorToAsync_WindowDoesNotEndWithTheMessage_RemovesNothingAndSaysSo()
    {
        // Dropping the last message blindly would silently discard real history; the mismatch means a
        // transport stopped storing the message before reading, which is worth a log line.
        Transcript(Msg(MessageRole.User, "a"), Msg(MessageRole.Assistant, "b"));

        var prior = await Read(10, Guid.NewGuid());

        prior.Select(m => m.Content).Should().Equal("a", "b");
        _logger.Warnings.Should().ContainSingle();
    }

    [Fact]
    public async Task ReadPriorToAsync_NeverReturnsMoreThanRequestedEvenWhenNothingMatched()
    {
        Transcript(Msg(MessageRole.Assistant, "1"), Msg(MessageRole.Assistant, "2"), Msg(MessageRole.Assistant, "3"));

        var prior = await Read(2, Guid.NewGuid());

        prior.Select(m => m.Content).Should().Equal("2", "3");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ReadPriorToAsync_NonPositiveWindow_ReturnsNothing_NotTheWholeTranscript(int max)
    {
        // The store defines a non-positive window as none; asking for one more than that must not turn
        // zero into "the in-flight message", or minus one into something unbounded.
        var sending = Msg(MessageRole.User, "c");
        Transcript(Msg(MessageRole.User, "a"), Msg(MessageRole.Assistant, "b"), sending);

        var prior = await Read(max, sending.Id);

        prior.Should().BeEmpty();
        _store.Verify(s => s.GetHistoryForDispatch("c1", "u1", 0, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReadPriorToAsync_UnboundedWindow_DoesNotOverflowIntoNoHistory()
    {
        // int.MaxValue is a plausible "no limit"; one more than that wraps negative, which the store reads as none.
        var sending = Msg(MessageRole.User, "c");
        Transcript(Msg(MessageRole.User, "a"), Msg(MessageRole.Assistant, "b"), sending);

        var prior = await Read(int.MaxValue, sending.Id);

        prior.Select(m => m.Content).Should().Equal("a", "b");
        _store.Verify(s => s.GetHistoryForDispatch("c1", "u1", int.MaxValue, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReadPriorToAsync_ConversationDoesNotExist_ReturnsEmpty()
    {
        _store
            .Setup(s => s.GetHistoryForDispatch("c1", "u1", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ConversationMessage>?)null);

        (await Read(10, Guid.NewGuid())).Should().BeEmpty();
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }
}
