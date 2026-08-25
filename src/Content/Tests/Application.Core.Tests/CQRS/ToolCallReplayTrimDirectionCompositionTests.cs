using Application.AI.Common.Models.Conversations;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Xunit;

namespace Application.Core.Tests.CQRS;

/// <summary>
/// Pins the one property neither half of the tool-call replay bound can prove on its own: that the
/// write-side per-turn cap and the read-side window budget trim from the <em>same</em> end, so the two
/// compose into a contiguous newest tail rather than a middle slice (#508).
/// </summary>
/// <remarks>
/// <para>
/// This test exists because the invariant was created and violated inside a single working session.
/// The write cap originally kept the earliest calls while the read budget kept the newest, each with a
/// comment justifying its own direction, and both halves had green tests — because each test only ever
/// exercised its own half. Composed, they produced a slice from the middle of a turn: neither the
/// reasoning that opened it nor the conclusions that closed it, while the assistant prose persisted
/// alongside referred to results that were no longer there.
/// </para>
/// <para>
/// Deliberately reproduces the write side by construction rather than by driving the real handler: the
/// handler needs an agent, an admission pipeline and a usage capture wired up, none of which bear on
/// the question here. What matters is that the two trims are applied in sequence, the way production
/// applies them — persistence first, then replay — and that the survivors are a suffix of what the turn
/// actually did. <c>ExecuteAgentTurnCommandHandlerTests</c> pins that the handler really does trim this
/// direction; this pins what that direction composes to.
/// </para>
/// </remarks>
public sealed class ToolCallReplayTrimDirectionCompositionTests
{
    private const int CostPerCall = 100;

    private static ToolCallRecord Call(int ordinal) =>
        new(
            "search",
            new string('i', CostPerCall / 2),
            new string('o', CostPerCall / 2),
            DurationMs: 0,
            CallId: $"call-{ordinal}",
            RoundOrdinal: ordinal);

    /// <summary>Mirrors <c>BuildTreatedToolCallRecords</c>' cap: keep the newest N by round ordinal.</summary>
    private static IReadOnlyList<ToolCallRecord> ApplyWriteSideCap(
        IReadOnlyList<ToolCallRecord> calls, int maxCallsPerTurn) =>
        calls.Count <= maxCallsPerTurn
            ? calls
            : calls.OrderBy(c => c.RoundOrdinal).TakeLast(maxCallsPerTurn).ToList();

    private static IReadOnlyList<string> ReplayedCallIds(IReadOnlyList<ChatMessage> replayed) =>
        replayed
            .SelectMany(m => m.Contents)
            .OfType<FunctionCallContent>()
            .Select(c => c.CallId)
            .ToList();

    [Fact]
    public void WriteCapThenReadBudget_SurvivorsAreAContiguousNewestTailOfTheOriginalTurn()
    {
        // A turn makes 10 calls. The write cap persists 6. The read budget then affords 3.
        var allCalls = Enumerable.Range(0, 10).Select(Call).ToList();

        var persisted = ApplyWriteSideCap(allCalls, maxCallsPerTurn: 6);
        persisted.Select(c => c.CallId).Should().Equal(
            ["call-4", "call-5", "call-6", "call-7", "call-8", "call-9"],
            "the write cap keeps the newest calls the turn made");

        var transcript = new List<ConversationMessage>
        {
            new(Guid.NewGuid(), MessageRole.Assistant, "done", DateTimeOffset.UtcNow, ToolCalls: persisted),
        };

        var replayed = ConversationMessageMapping.ToChatMessages(
            transcript, replayToolCalls: true, maxReplayedChars: 3 * CostPerCall);

        ReplayedCallIds(replayed).Should().Equal(
            ["call-7", "call-8", "call-9"],
            "trimming the same end twice leaves a suffix of the original turn — the composition a " +
            "middle slice would not be");
    }

    [Fact]
    public void WriteCapThenReadBudget_SurvivorsAreNeverDiscontiguous()
    {
        // The property stated generally, across every pairing of the two limits: whatever survives must
        // be a CONTIGUOUS run ending at the turn's last call. A middle slice — the failure this guards —
        // would satisfy neither clause.
        var allCalls = Enumerable.Range(0, 10).Select(Call).ToList();

        for (var cap = 1; cap <= 10; cap++)
        {
            for (var affordable = 1; affordable <= 10; affordable++)
            {
                var persisted = ApplyWriteSideCap(allCalls, cap);
                var transcript = new List<ConversationMessage>
                {
                    new(Guid.NewGuid(), MessageRole.Assistant, "done", DateTimeOffset.UtcNow,
                        ToolCalls: persisted),
                };

                var survivors = ReplayedCallIds(ConversationMessageMapping.ToChatMessages(
                    transcript, replayToolCalls: true, maxReplayedChars: affordable * CostPerCall));

                var expectedCount = Math.Min(cap, affordable);
                var expected = allCalls
                    .TakeLast(expectedCount)
                    .Select(c => c.CallId)
                    .ToList();

                survivors.Should().Equal(expected,
                    $"cap={cap}, affordable={affordable}: the survivors must be the newest " +
                    $"{expectedCount} calls of the turn, contiguous and ending at the last one");
            }
        }
    }
}
