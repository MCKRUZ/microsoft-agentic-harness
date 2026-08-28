using Application.AI.Common.Models.Conversations;
using FluentAssertions;
using Microsoft.Extensions.AI;

namespace Application.AI.Common.Tests.Models.Conversations;

/// <summary>
/// Proves the read-side half of #508: <see cref="ConversationMessageMapping.ToChatMessages(IReadOnlyList{ConversationMessage},bool,int,Microsoft.Extensions.Logging.ILogger?)"/> bounds
/// the total treated tool-call text one replayed window sends to the model, dropping oldest-first.
/// </summary>
/// <remarks>
/// <para>
/// Why a read-side bound is needed at all, given the write-side per-turn cap exists too: the store's
/// dispatch window is capped in <em>rows</em>, and each row expands here into two chat messages per
/// tool call it carries, so a 50-row window of tool-heavy turns expands without bound. The write-side
/// cap cannot cover rows persisted before that cap existed; this one can, because it applies at replay.
/// </para>
/// <para>
/// Separate file from <see cref="ConversationMessageMappingTests"/>, which is about expansion
/// correctness and deliberately leaves the budget at its <see cref="int.MaxValue"/> default.
/// </para>
/// </remarks>
public sealed class ConversationMessageMappingBudgetTests
{
    private const string ToolName = "search";

    /// <summary>
    /// A call whose <em>total</em> budget cost is exactly <paramref name="cost"/> characters.
    /// </summary>
    /// <remarks>
    /// The payload is sized down by the tool name and call id, because those count against the budget
    /// too — they reach the model and no treatment pass bounds them. Sizing the payload alone would
    /// leave every arithmetic assertion below quietly off by the length of two strings, which is
    /// exactly the kind of drift that makes a budget test stop testing the budget.
    /// </remarks>
    private static ToolCallRecord Call(string id, int ordinal, int cost)
    {
        var payload = cost - ToolName.Length - id.Length;
        if (payload < 0)
            throw new ArgumentOutOfRangeException(nameof(cost), cost, "Cost must cover the name and id.");

        return new ToolCallRecord(
            ToolName,
            new string('i', payload / 2),
            new string('o', payload - (payload / 2)),
            DurationMs: 0,
            CallId: id,
            RoundOrdinal: ordinal);
    }

    private static ConversationMessage AssistantRow(string text, params ToolCallRecord[] calls) =>
        new(Guid.NewGuid(), MessageRole.Assistant, text, DateTimeOffset.UtcNow, ToolCalls: calls);

    private static IReadOnlyList<string> ReplayedCallIds(IReadOnlyList<ChatMessage> replayed) =>
        replayed
            .SelectMany(m => m.Contents)
            .OfType<FunctionCallContent>()
            .Select(c => c.CallId)
            .ToList();

    [Fact]
    public void ToChatMessages_TotalUnderBudget_ReplaysEveryCall()
    {
        var transcript = new List<ConversationMessage>
        {
            AssistantRow("first", Call("call-1", 0, 100)),
            AssistantRow("second", Call("call-2", 0, 100)),
        };

        var replayed = ConversationMessageMapping.ToChatMessages(transcript, replayToolCalls: true, maxReplayedChars: 1000);

        ReplayedCallIds(replayed).Should().Equal("call-1", "call-2");
    }

    [Fact]
    public void ToChatMessages_TotalOverBudget_DropsOldestAndKeepsNewest()
    {
        var transcript = new List<ConversationMessage>
        {
            AssistantRow("first", Call("call-1", 0, 100)),
            AssistantRow("second", Call("call-2", 0, 100)),
            AssistantRow("third", Call("call-3", 0, 100)),
        };

        // Room for two of the three.
        var replayed = ConversationMessageMapping.ToChatMessages(transcript, replayToolCalls: true, maxReplayedChars: 250);

        ReplayedCallIds(replayed).Should().Equal(
            ["call-2", "call-3"],
            "a replayed window is context, not an audit log — the most recent tool activity is what " +
            "the current turn is most likely to reason about");
    }

    [Fact]
    public void ToChatMessages_DroppedRowStillReplaysItsNarratedText()
    {
        var transcript = new List<ConversationMessage>
        {
            AssistantRow("I searched and found nothing", Call("call-1", 0, 100)),
            AssistantRow("second", Call("call-2", 0, 100)),
        };

        var replayed = ConversationMessageMapping.ToChatMessages(transcript, replayToolCalls: true, maxReplayedChars: 100);

        ReplayedCallIds(replayed).Should().Equal("call-2");
        replayed.Select(m => m.Text).Should().Contain("I searched and found nothing",
            "losing the literal call/result pair must not also lose the prose account of what happened");
    }

    [Fact]
    public void ToChatMessages_BudgetSmallerThanNewestCall_AdmitsNothing()
    {
        var transcript = new List<ConversationMessage>
        {
            AssistantRow("only", Call("call-1", 0, 100)),
        };

        var replayed = ConversationMessageMapping.ToChatMessages(transcript, replayToolCalls: true, maxReplayedChars: 10);

        ReplayedCallIds(replayed).Should().BeEmpty(
            "always keeping one call would mean the bound can be exceeded by an unbounded amount, " +
            "which is not a bound");
        replayed.Should().ContainSingle().Which.Text.Should().Be("only");
    }

    [Fact]
    public void ToChatMessages_ZeroBudget_ReplaysTextOnly()
    {
        var transcript = new List<ConversationMessage>
        {
            AssistantRow("narration", Call("call-1", 0, 100)),
        };

        var replayed = ConversationMessageMapping.ToChatMessages(transcript, replayToolCalls: true, maxReplayedChars: 0);

        ReplayedCallIds(replayed).Should().BeEmpty();
        replayed.Should().ContainSingle().Which.Text.Should().Be("narration");
    }

    [Fact]
    public void ToChatMessages_PartialRow_DropsOldestCallsWithinThatRow()
    {
        var transcript = new List<ConversationMessage>
        {
            AssistantRow(
                "one turn, three parallel calls",
                Call("call-1", 0, 100),
                Call("call-2", 1, 100),
                Call("call-3", 2, 100)),
        };

        var replayed = ConversationMessageMapping.ToChatMessages(transcript, replayToolCalls: true, maxReplayedChars: 250);

        ReplayedCallIds(replayed).Should().Equal(
            ["call-2", "call-3"],
            "the budget cuts within a row as readily as across rows");
    }

    [Fact]
    public void ToChatMessages_AdmissionLatchesShut_RatherThanSkippingToASmallerOlderCall()
    {
        var transcript = new List<ConversationMessage>
        {
            AssistantRow("tiny and old", Call("call-1", 0, 20)),
            AssistantRow("huge", Call("call-2", 0, 400)),
            AssistantRow("newest", Call("call-3", 0, 100)),
        };

        // call-3 (100) fits. call-2 (400) does not. call-1 (20) would fit in the 100 that remains, but
        // admitting it would replay a sequence that never happened — call-1 then call-3, with the
        // call-2 that came between them silently gone.
        var replayed = ConversationMessageMapping.ToChatMessages(transcript, replayToolCalls: true, maxReplayedChars: 200);

        ReplayedCallIds(replayed).Should().Equal(
            ["call-3"],
            "the surviving set must be a contiguous newest tail, not holes punched through history");
    }

    [Fact]
    public void ToChatMessages_EveryDroppedCallLosesItsResultToo()
    {
        var transcript = new List<ConversationMessage>
        {
            AssistantRow("first", Call("call-1", 0, 100)),
            AssistantRow("second", Call("call-2", 0, 100)),
        };

        var replayed = ConversationMessageMapping.ToChatMessages(transcript, replayToolCalls: true, maxReplayedChars: 100);

        var results = replayed
            .SelectMany(m => m.Contents)
            .OfType<FunctionResultContent>()
            .Select(r => r.CallId)
            .ToList();

        results.Should().Equal(
            ["call-2"],
            "a call is dropped as a whole call/result pair — an assistant tool_calls entry with no " +
            "matching result is a malformed conversation a provider rejects outright");
    }

    [Fact]
    public void ToChatMessages_ReplayDisabled_IgnoresBudgetAndReplaysTextOnly()
    {
        var transcript = new List<ConversationMessage>
        {
            AssistantRow("narration", Call("call-1", 0, 100)),
        };

        var replayed = ConversationMessageMapping.ToChatMessages(
            transcript, replayToolCalls: false, maxReplayedChars: 0);

        replayed.Should().ContainSingle().Which.Text.Should().Be("narration");
    }
}
