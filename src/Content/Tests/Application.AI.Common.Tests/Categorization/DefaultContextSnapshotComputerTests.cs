using Application.AI.Common.Categorization;
using Application.AI.Common.Helpers;
using Domain.AI.Context;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Xunit;

namespace Application.AI.Common.Tests.Categorization;

/// <summary>
/// Pins the measured derivation (#507): registration categories pass through from what was actually
/// loaded, and <c>messages</c> is estimated from the post-turn history. Nothing is derived by
/// subtracting from the provider's reported usage.
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
    public void Compute_LargeTranscriptEstimate_CannotErodeTheMeasuredSystemPrompt()
    {
        // The #507 scenario, asserted the other way round. The old implementation derived the system
        // lane by subtracting the transcript estimate from the provider's billed total and clamping —
        // so an estimate that ran long (the ~4-chars-per-token rule does, on JSON-shaped tool payloads)
        // reported NO system prompt at all. Here the two quantities never meet: the system lane is
        // measured, and nothing about the transcript can touch it.
        var history = new List<ChatMessage> { new(ChatRole.User, new string('a', 4_000)) };

        var snapshot = _sut.Compute(
            conversationId: "conv-1",
            turnIndex: 0,
            turnId: "t-00",
            history: history,
            registrations: Registrations(system: 750),
            turnLoaded: [],
            capturedAtUtc: _now);

        snapshot.CtxAfter.System.Should().Be(750,
            "a measured system prompt cannot be erased by a disagreement about the transcript");
        snapshot.CtxAfter.Messages.Should().Be(1_000);
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
            history: history,
            registrations: new CategoryBreakdown(100, 0, 0, 0, 0, Messages: 9_999),
            turnLoaded: [],
            capturedAtUtc: _now);

        snapshot.CtxAfter.Messages.Should().Be(100, "the transcript is measured here, from history");
    }

    [Fact]
    public void Compute_NothingRegistered_ClaimsNothingRatherThanInventingASystemPrompt()
    {
        // Under the old derivation this turn reported the entire billed total as "System", because
        // that lane was defined as whatever was left over. An empty registration state means nothing
        // is KNOWN to be in the prompt, and an honest breakdown says so rather than filling the gap.
        var snapshot = _sut.Compute(
            conversationId: "conv-1",
            turnIndex: 0,
            turnId: "t-00",
            history: [],
            registrations: CategoryBreakdown.Empty,
            turnLoaded: [],
            capturedAtUtc: _now);

        snapshot.CtxAfter.Total.Should().Be(0);
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
            history: [],
            registrations: CategoryBreakdown.Empty,
            turnLoaded: [],
            capturedAtUtc: _now);

        snapshot.CtxAfter.Messages.Should().Be(0);
        snapshot.CtxAfter.Total.Should().Be(0,
            "nothing was registered and nothing was said — the bar claims nothing rather than "
            + "inventing a system-prompt figure from the billed total");
    }

    [Fact]
    public void Compute_NullConversationId_Throws()
    {
        Action act = () => _sut.Compute(
            conversationId: "",
            turnIndex: 0,
            turnId: "t-00",
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
            history: [],
            registrations: null!,
            turnLoaded: [],
            capturedAtUtc: _now);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Compute_LastCallPromptTokensOmitted_UnattributedTokensIsNull()
    {
        // A turn with no model call (a failed turn, or one with only local work) has nothing to
        // reconcile against — every existing test above omits this parameter and must keep passing.
        var snapshot = _sut.Compute(
            conversationId: "conv-1",
            turnIndex: 0,
            turnId: "t-00",
            history: [],
            registrations: CategoryBreakdown.Empty,
            turnLoaded: [],
            capturedAtUtc: _now);

        snapshot.UnattributedTokens.Should().BeNull();
    }

    [Fact]
    public void Compute_LastCallPromptTokensExceedsCtxAfter_UnattributedTokensIsPositive()
    {
        // #517: context reached the model that no lane explains — the case the original,
        // removed #507 attempt existed to surface, now computed against operands pinned to the
        // same call and the same side of the turn boundary instead of an accumulated total.
        var snapshot = _sut.Compute(
            conversationId: "conv-1",
            turnIndex: 0,
            turnId: "t-00",
            history: [],
            registrations: Registrations(system: 1_000),
            turnLoaded: [],
            capturedAtUtc: _now,
            lastCallPromptTokens: 1_500);

        snapshot.CtxAfter.Total.Should().Be(1_000);
        snapshot.UnattributedTokens.Should().Be(500);
    }

    [Fact]
    public void Compute_LastCallPromptTokensUndershootsCtxAfter_UnattributedTokensIsNegative()
    {
        // The bar's own estimates overshot the real prompt — a signed value is what makes this
        // direction distinguishable from the positive case, which a seventh additive ContextCategory
        // could never represent.
        var snapshot = _sut.Compute(
            conversationId: "conv-1",
            turnIndex: 0,
            turnId: "t-00",
            history: [],
            registrations: Registrations(system: 2_000),
            turnLoaded: [],
            capturedAtUtc: _now,
            lastCallPromptTokens: 1_700);

        snapshot.UnattributedTokens.Should().Be(-300);
    }

    [Fact]
    public void Compute_LastCallPromptTokensExactlyMatchesCtxAfter_UnattributedTokensIsZero()
    {
        var snapshot = _sut.Compute(
            conversationId: "conv-1",
            turnIndex: 0,
            turnId: "t-00",
            history: [],
            registrations: Registrations(system: 900),
            turnLoaded: [],
            capturedAtUtc: _now,
            lastCallPromptTokens: 900);

        snapshot.UnattributedTokens.Should().Be(0);
    }
}
