using Application.AI.Common.Exceptions;
using Application.AI.Common.Models.Conversations;
using Application.Core.CQRS.Agents.ExecuteAgentTurn;
using FluentAssertions;
using Moq;
using Presentation.AgentHub.DTOs;
using Xunit;

namespace Presentation.AgentHub.Tests.Services;

/// <summary>
/// After a failed or refused turn the client's message list and the conversation must still name the same messages, or a
/// later retry or edit cuts the client's list in the wrong place (#961). The hub tells the client what changed: the
/// message a turn that was not admitted never stored, the error row a failed turn did store, and, on a retry or edit,
/// the first message the truncation removed.
/// </summary>
public sealed partial class ScreeningRefusalReplayTests
{
    [Fact]
    public async Task ARefusedSend_ThenARetryOfAnEarlierReply_CutsAtThatReply()
    {
        // The case #961 names: the refused message never reached the store, and the client was told to drop it, so the
        // retry's notice must name the reply both sides hold, not a position one side counts differently.
        var conversation = await SeedExchangeAsync("the question");
        var calls = 0;
        _mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .Returns<ExecuteAgentTurnCommand, CancellationToken>((_, _) => calls++ == 0
                ? Task.FromException<AgentTurnResult>(new ContentSafetyException("Content blocked by safety policy (hate).", "hate"))
                : AdmitThen(Answer("a better answer")));
        var orchestrator = Orchestrator();
        var truncations = new List<HistoryTruncation>();

        var refusedMessageId = Guid.NewGuid();
        var refused = await orchestrator.SendMessageAsync(
            "conn", conversation.Id, refusedMessageId, Refused, "user-a", null, CancellationToken.None);
        var retried = await orchestrator.RetryFromMessageAsync(
            "conn", conversation.Id, conversation.AssistantMessageId, "user-a", null, CancellationToken.None,
            RecordTruncation(truncations));

        refused.RemovedMessageId.Should().Be(refusedMessageId);
        retried.Success.Should().BeTrue();
        truncations.Should().Equal(new HistoryTruncation(conversation.AssistantMessageId, 1));
        (await ContentsAsync(conversation.Id)).Should().Equal("the question", "a better answer");
    }

    [Fact]
    public async Task AFailedTurn_ThenARetryOfItsErrorRow_CutsAtThatRow()
    {
        // The client adds the error row under the id the outcome names, so retrying from that row must find it in the
        // conversation; under a client-made id the retry would fall back to the last message and cut nothing.
        var conversation = await _store.CreateAsync("agent", "user-a");
        var calls = 0;
        _mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .Returns<ExecuteAgentTurnCommand, CancellationToken>((_, _) => calls++ == 0
                ? AdmitThen(Task.FromException<AgentTurnResult>(new InvalidOperationException("the model failed")))
                : AdmitThen(Answer("an answer")));
        var orchestrator = Orchestrator();
        var truncations = new List<HistoryTruncation>();

        var failed = await orchestrator.SendMessageAsync(
            "conn", conversation.Id, Guid.NewGuid(), "the question", "user-a", null, CancellationToken.None);
        var stored = await ContentsAsync(conversation.Id);
        await orchestrator.RetryFromMessageAsync(
            "conn", conversation.Id, failed.ErrorRow!.Id, "user-a", null, CancellationToken.None,
            RecordTruncation(truncations));

        failed.ErrorRow.Content.Should().Be(stored[^1], "the client shows the row the conversation holds");
        truncations.Should().Equal(new HistoryTruncation(failed.ErrorRow.Id, 1));
        (await ContentsAsync(conversation.Id)).Should().Equal("the question", "an answer");
    }

    [Fact]
    public async Task ARetryOfAMessageTheConversationDoesNotHold_NamesNothingRemoved()
    {
        // The store's truncation is a no-op for an id it does not hold; naming that id would have the client cut a
        // message the conversation kept.
        var conversation = await SeedExchangeAsync("the question");
        await _store.AppendMessageAsync(conversation.Id, "user-a",
            new(Guid.NewGuid(), MessageRole.User, "a follow-up", DateTimeOffset.UtcNow));
        _mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .Returns(() => AdmitThen(Answer("an answer to the follow-up")));
        var truncations = new List<HistoryTruncation>();

        await Orchestrator().RetryFromMessageAsync(
            "conn", conversation.Id, Guid.NewGuid(), "user-a", null, CancellationToken.None, RecordTruncation(truncations));

        truncations.Should().Equal(new HistoryTruncation(null, 3));
    }

    [Fact]
    public async Task AnEditOfAMessageTheConversationDoesNotHold_NamesNothingRemoved()
    {
        var conversation = await SeedExchangeAsync("the question");
        _mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .Returns(() => AdmitThen(Answer("an answer")));
        var truncations = new List<HistoryTruncation>();

        await Orchestrator().EditAndResubmitAsync(
            "conn", conversation.Id, Guid.NewGuid(), Guid.NewGuid(), "an edit", "user-a", null, CancellationToken.None,
            RecordTruncation(truncations));

        truncations.Should().Equal(new HistoryTruncation(null, 2));
        (await ContentsAsync(conversation.Id)).Should().Equal("the question", "an answer", "an edit", "an answer");
    }
}
