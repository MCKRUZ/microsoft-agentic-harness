using Application.AI.Common.Categorization;
using Application.AI.Common.Helpers;
using Domain.AI.Context;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Xunit;

namespace Application.AI.Common.Tests.Categorization;

/// <summary>
/// Pins the PR 3 v1 derivation: <c>messages</c> from the post-turn history,
/// <c>system</c> as the residual of <c>inputTokens − messages</c>, all other
/// categories zero.
/// </summary>
public sealed class DefaultContextSnapshotComputerTests
{
    private readonly DefaultContextSnapshotComputer _sut = new();
    private readonly DateTimeOffset _now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Compute_TypicalTurn_DerivesSystemAsResidualOfInputMinusMessages()
    {
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "tell me a joke"),
            new(ChatRole.Assistant, "why did the chicken cross the road"),
        };
        var messageTokens = TokenEstimationHelper.EstimateTokens(history);
        const int inputTokens = 8_200;

        var snapshot = _sut.Compute(
            conversationId: "conv-1",
            turnIndex: 0,
            turnId: "t-00",
            inputTokens: inputTokens,
            history: history,
            turnLoaded: [],
            capturedAtUtc: _now);

        snapshot.CtxAfter.Messages.Should().Be(messageTokens);
        snapshot.CtxAfter.System.Should().Be(inputTokens - messageTokens);
        snapshot.CtxAfter.Agents.Should().Be(0);
        snapshot.CtxAfter.Skills.Should().Be(0);
        snapshot.CtxAfter.Tools.Should().Be(0);
        snapshot.CtxAfter.Mcp.Should().Be(0);
    }

    [Fact]
    public void Compute_InputTokensSmallerThanMessages_ClampsSystemToZero()
    {
        // Reproduces a degenerate case: the harness reports fewer input tokens
        // than the message-history estimate (model tokenizer disagrees with
        // the 4-char heuristic). System should clamp to 0, never go negative.
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, new string('a', 400)), // ≈100 tokens via heuristic
        };

        var snapshot = _sut.Compute(
            conversationId: "conv-1",
            turnIndex: 0,
            turnId: "t-00",
            inputTokens: 20,
            history: history,
            turnLoaded: [],
            capturedAtUtc: _now);

        snapshot.CtxAfter.System.Should().Be(0);
        snapshot.CtxAfter.Messages.Should().Be(100);
    }

    [Fact]
    public void Compute_PassesThroughTurnLoadedItemsAndIdentityFields()
    {
        var loaded = new[]
        {
            new LoadedItem("User message", 50, ContextCategory.Messages, null),
            new LoadedItem("Assistant message", 80, ContextCategory.Messages, null),
        };

        var snapshot = _sut.Compute(
            conversationId: "conv-1",
            turnIndex: 4,
            turnId: "t-04",
            inputTokens: 1_000,
            history: [],
            turnLoaded: loaded,
            capturedAtUtc: _now);

        snapshot.ConversationId.Should().Be("conv-1");
        snapshot.TurnIndex.Should().Be(4);
        snapshot.TurnId.Should().Be("t-04");
        snapshot.Loaded.Should().BeEquivalentTo(loaded);
        snapshot.CapturedAtUtc.Should().Be(_now);
    }

    [Fact]
    public void Compute_EmptyHistory_MessagesIsZero()
    {
        var snapshot = _sut.Compute(
            conversationId: "conv-1",
            turnIndex: 0,
            turnId: "t-00",
            inputTokens: 500,
            history: [],
            turnLoaded: [],
            capturedAtUtc: _now);

        snapshot.CtxAfter.Messages.Should().Be(0);
        snapshot.CtxAfter.System.Should().Be(500);
        snapshot.CtxAfter.Total.Should().Be(500);
    }

    [Fact]
    public void Compute_NullConversationId_Throws()
    {
        Action act = () => _sut.Compute(
            conversationId: "",
            turnIndex: 0,
            turnId: "t-00",
            inputTokens: 0,
            history: [],
            turnLoaded: [],
            capturedAtUtc: _now);

        act.Should().Throw<ArgumentException>();
    }
}
