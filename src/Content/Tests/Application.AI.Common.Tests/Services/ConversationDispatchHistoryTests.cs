using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Models.Conversations;
using Application.AI.Common.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace Application.AI.Common.Tests.Services;

/// <summary>
/// <see cref="ConversationDispatchHistory"/> (#785, #964): the history a turn is dispatched with holds only the messages
/// before the one being sent, which is stored only once the turn is admitted, and stops before the part an edit or a
/// retry replaces, which is still stored.
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

    private Task<IReadOnlyList<ConversationMessage>> Read(int max, Guid? before = null) =>
        ConversationDispatchHistory.ReadPriorAsync(
            _store.Object, "c1", "u1", max, before, _logger, CancellationToken.None);

    [Fact]
    public async Task ReadPriorAsync_ANewMessage_ReturnsTheLatestMessages()
    {
        Transcript(Msg(MessageRole.User, "1"), Msg(MessageRole.Assistant, "2"), Msg(MessageRole.User, "3"));

        var prior = await Read(2);

        prior.Select(m => m.Content).Should().Equal("2", "3");
        _logger.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadPriorAsync_AnEditOrRetry_StopsBeforeTheMessageItReplaces()
    {
        // Until admission the replaced part is still stored; the model must not see it as history (#962).
        var replaced = Msg(MessageRole.User, "original");
        Transcript(Msg(MessageRole.User, "a"), Msg(MessageRole.Assistant, "b"), replaced, Msg(MessageRole.Assistant, "old answer"));

        var prior = await Read(10, replaced.Id);

        prior.Select(m => m.Content).Should().Equal("a", "b");
        _logger.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadPriorAsync_AnEditFarBack_FindsTheMessageBeyondTheWindowAndCapsWhatPrecedesIt()
    {
        // The read takes the whole conversation, so an edit of an old message still finds it; only the messages before
        // it are capped.
        var replaced = Msg(MessageRole.User, "old");
        Transcript(Msg(MessageRole.User, "1"), Msg(MessageRole.Assistant, "2"), Msg(MessageRole.User, "3"), replaced,
            Msg(MessageRole.Assistant, "4"), Msg(MessageRole.User, "5"), Msg(MessageRole.Assistant, "6"));

        var prior = await Read(2, replaced.Id);

        prior.Select(m => m.Content).Should().Equal("2", "3");
    }

    [Fact]
    public async Task ReadPriorAsync_MatchesByIdNotByContent()
    {
        var replaced = Msg(MessageRole.User, "same text");
        Transcript(Msg(MessageRole.User, "same text"), Msg(MessageRole.Assistant, "b"), replaced);

        var prior = await Read(10, replaced.Id);

        prior.Select(m => m.Content).Should().Equal("same text", "b");
    }

    [Fact]
    public async Task ReadPriorAsync_AMessageTheConversationDoesNotHold_ReturnsTheLatestAndSaysSo()
    {
        Transcript(Msg(MessageRole.User, "a"), Msg(MessageRole.Assistant, "b"), Msg(MessageRole.User, "c"));

        var prior = await Read(2, Guid.NewGuid());

        prior.Select(m => m.Content).Should().Equal("b", "c");
        _logger.Warnings.Should().ContainSingle();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ReadPriorAsync_NonPositiveWindow_ReturnsNothing_NotTheWholeTranscript(int max)
    {
        var replaced = Msg(MessageRole.User, "c");
        Transcript(Msg(MessageRole.User, "a"), Msg(MessageRole.Assistant, "b"), replaced);

        (await Read(max)).Should().BeEmpty();
        (await Read(max, replaced.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task ReadPriorAsync_UnboundedWindow_ReturnsEverything()
    {
        Transcript(Msg(MessageRole.User, "a"), Msg(MessageRole.Assistant, "b"));

        var prior = await Read(int.MaxValue);

        prior.Select(m => m.Content).Should().Equal("a", "b");
    }

    [Fact]
    public async Task ReadPriorAsync_ConversationDoesNotExist_ReturnsEmpty()
    {
        _store
            .Setup(s => s.GetHistoryForDispatch("c1", "u1", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ConversationMessage>?)null);

        (await Read(10)).Should().BeEmpty();
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
