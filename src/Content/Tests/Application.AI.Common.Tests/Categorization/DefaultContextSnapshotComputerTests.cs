using Application.AI.Common.Categorization;
using Application.AI.Common.Helpers;
using Domain.AI.Context;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Xunit;

namespace Application.AI.Common.Tests.Categorization;

/// <summary>
/// Pins the measured derivation (#507): registration categories pass through from what was actually
/// loaded, <c>messages</c> is estimated from the post-turn history, and the provider's reported total
/// is recorded for reconciliation rather than subtracted from to invent a category.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This file previously asserted the defect.</strong> A test named
/// <c>Compute_InputTokensSmallerThanMessages_ClampsSystemToZero</c> pinned the exact behaviour #507
/// was filed about — the system-prompt lane reporting zero because an overshooting transcript estimate
/// had been subtracted from the billed total — and a sibling pinned <c>system</c> as that residual.
/// Both passed. A test can encode a bug as an invariant just as faithfully as it encodes a
/// requirement, and then defend it; the replacement below asserts the opposite in the same scenario.
/// </para>
/// </remarks>
public sealed class DefaultContextSnapshotComputerTests
{
    private readonly DefaultContextSnapshotComputer _sut = new();
    private readonly DateTimeOffset _now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static CategoryBreakdown Registrations(
        int system = 0, int agents = 0, int skills = 0, int tools = 0, int mcp = 0) =>
        new(system, agents, skills, tools, mcp, Messages: 0);

    [Fact]
    public void Compute_TypicalTurn_ReportsEveryRegistrationCategoryAsMeasured()
    {
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "tell me a joke"),
            new(ChatRole.Assistant, "why did the chicken cross the road"),
        };
        var messageTokens = TokenEstimationHelper.EstimateTokens(history);

        var snapshot = _sut.Compute(
            conversationId: "conv-1",
            turnIndex: 0,
            turnId: "t-00",
            inputTokens: 8_200,
            history: history,
            registrations: Registrations(system: 1_200, agents: 90, skills: 400, tools: 650, mcp: 120),
            turnLoaded: [],
            capturedAtUtc: _now);

        snapshot.CtxAfter.System.Should().Be(1_200);
        snapshot.CtxAfter.Agents.Should().Be(90);
        snapshot.CtxAfter.Skills.Should().Be(400);
        snapshot.CtxAfter.Tools.Should().Be(650);
        snapshot.CtxAfter.Mcp.Should().Be(120);
        snapshot.CtxAfter.Messages.Should().Be(messageTokens,
            "the transcript is the one category still estimated rather than measured");
    }

    [Fact]
    public void Compute_EstimateOvershootsBilledTotal_KeepsSystemAndReportsANegativeGap()
    {
        // The #507 scenario, asserted the other way round. A tool-heavy turn makes the
        // ~4-chars-per-token transcript estimate exceed what the provider actually billed. The old
        // implementation subtracted one from the other and clamped, reporting NO system prompt — here
        // the system prompt survives untouched because nothing subtracts from it, and the overshoot
        // surfaces as a negative gap instead of being absorbed into a category.
        var history = new List<ChatMessage> { new(ChatRole.User, new string('a', 400)) };

        var snapshot = _sut.Compute(
            conversationId: "conv-1",
            turnIndex: 0,
            turnId: "t-00",
            inputTokens: 20,
            history: history,
            registrations: Registrations(system: 750),
            turnLoaded: [],
            capturedAtUtc: _now);

        snapshot.CtxAfter.System.Should().Be(750,
            "a measured system prompt cannot be erased by a disagreement about the transcript");
        snapshot.CtxAfter.Messages.Should().Be(100);
        snapshot.MeasuredInputTokens.Should().Be(20);
        snapshot.UnaccountedTokens.Should().Be(20 - 850,
            "an estimate that ran long is reported as a negative gap, which is why this is a derived "
            + "difference and not a seventh category — a category cannot hold it");
    }

    [Fact]
    public void Compute_RegistrationsCarryMessages_IgnoresThemRatherThanDoubleCounting()
    {
        // A caller passing a populated Messages total is a plausible mistake: registrations and the
        // transcript are both "context". Counting both would inflate the bar by the whole transcript
        // and look entirely reasonable on screen.
        var history = new List<ChatMessage> { new(ChatRole.User, new string('a', 400)) };

        var snapshot = _sut.Compute(
            conversationId: "conv-1",
            turnIndex: 0,
            turnId: "t-00",
            inputTokens: 1_000,
            history: history,
            registrations: new CategoryBreakdown(100, 0, 0, 0, 0, Messages: 9_999),
            turnLoaded: [],
            capturedAtUtc: _now);

        snapshot.CtxAfter.Messages.Should().Be(100, "the transcript is measured here, from history");
    }

    [Fact]
    public void Compute_NoUsageReported_LeavesTheGapAtZeroRatherThanClaimingEverythingIsUnaccounted()
    {
        // inputTokens == 0 means "no usage was reported", not "the prompt was empty". Reporting the
        // whole attributed total as an unaccounted deficit would be a large, confident, wrong number.
        var snapshot = _sut.Compute(
            conversationId: "conv-1",
            turnIndex: 0,
            turnId: "t-00",
            inputTokens: 0,
            history: [],
            registrations: Registrations(system: 500),
            turnLoaded: [],
            capturedAtUtc: _now);

        snapshot.MeasuredInputTokens.Should().Be(0);
        snapshot.UnaccountedTokens.Should().Be(0);
        snapshot.CtxAfter.System.Should().Be(500, "the breakdown stands on its own measurements");
    }

    [Fact]
    public void Compute_ContextReachedTheModelThatNoCategoryExplains_ShowsAPositiveGap()
    {
        var snapshot = _sut.Compute(
            conversationId: "conv-1",
            turnIndex: 0,
            turnId: "t-00",
            inputTokens: 5_000,
            history: [],
            registrations: Registrations(system: 1_000, tools: 500),
            turnLoaded: [],
            capturedAtUtc: _now);

        snapshot.CtxAfter.Total.Should().Be(1_500);
        snapshot.UnaccountedTokens.Should().Be(3_500,
            "provider-side framing and anything the harness never registered is surfaced, not hidden");
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
            registrations: CategoryBreakdown.Empty,
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
            registrations: CategoryBreakdown.Empty,
            turnLoaded: [],
            capturedAtUtc: _now);

        snapshot.CtxAfter.Messages.Should().Be(0);
        snapshot.CtxAfter.Total.Should().Be(0,
            "nothing was registered and nothing was said — the bar claims nothing rather than "
            + "inventing a system-prompt figure from the billed total");
        snapshot.UnaccountedTokens.Should().Be(500);
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
            registrations: CategoryBreakdown.Empty,
            turnLoaded: [],
            capturedAtUtc: _now);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Compute_NullRegistrations_Throws()
    {
        Action act = () => _sut.Compute(
            conversationId: "conv-1",
            turnIndex: 0,
            turnId: "t-00",
            inputTokens: 0,
            history: [],
            registrations: null!,
            turnLoaded: [],
            capturedAtUtc: _now);

        act.Should().Throw<ArgumentNullException>();
    }
}
