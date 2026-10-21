using Application.AI.Common.Services.AI;
using Domain.Common.Compliance;
using Domain.Common.Config;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Services.AI;

/// <summary>
/// Right-to-erasure (#900) for <see cref="InProcessConversationBudgetTracker"/>: the running totals of the person's
/// conversations are released, and every other entry — another person's conversation, a plan run's key — keeps its total.
/// </summary>
/// <remarks>
/// Over-deletion destroys another person's data and, here, also hands a live conversation a fresh ceiling; under-deletion
/// leaves the person's data behind while the erasure receipt claims success. Entries are keyed by conversation id only,
/// so the subject's conversation ids are the whole match.
/// </remarks>
public sealed class InProcessConversationBudgetTrackerErasureTests
{
    private readonly InProcessConversationBudgetTracker _tracker;

    public InProcessConversationBudgetTrackerErasureTests()
    {
        var cfg = new AppConfig();
        cfg.AI.AgentFramework.ConversationTokenBudget = 10_000;
        _tracker = new InProcessConversationBudgetTracker(
            Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == cfg),
            TimeProvider.System,
            NullLogger<InProcessConversationBudgetTracker>.Instance);
    }

    [Fact]
    public async Task EraseSubjectAsync_SubjectsConversations_ReleasesOnlyTheirEntries()
    {
        await _tracker.RecordUsageAsync("conv-a", 100);
        await _tracker.RecordUsageAsync("conv-b", 200);
        await _tracker.RecordUsageAsync("conv-other", 300);
        await _tracker.RecordUsageAsync("planrun:run-1", 400);
        await _tracker.RecordUsageAsync("CONV-A", 500);
        (await ConsumedAsync("conv-a")).Should().Be(100, "the seed must exist before erasing");

        var removed = await _tracker.EraseSubjectAsync(
            new ErasureSubject("user-a", ["conv-a", "conv-b", "conv-never-spent"]), CancellationToken.None);

        removed.Should().Be(2, "only entries that existed are counted");
        (await ConsumedAsync("conv-a")).Should().Be(0);
        (await ConsumedAsync("conv-b")).Should().Be(0);
        (await ConsumedAsync("conv-other")).Should().Be(300, "another conversation keeps its total");
        (await ConsumedAsync("planrun:run-1")).Should().Be(400, "a plan run's key is not a conversation");
        (await ConsumedAsync("CONV-A")).Should().Be(500, "keys are compared exactly; a differently-cased id is another key");
    }

    [Fact]
    public async Task EraseSubjectAsync_NoConversations_ReturnsZeroAndChangesNothing()
    {
        await _tracker.RecordUsageAsync("conv-other", 300);

        (await _tracker.EraseSubjectAsync(new ErasureSubject("user-a", []), CancellationToken.None)).Should().Be(0);
        (await _tracker.EraseSubjectAsync(new ErasureSubject("user-a", ["conv-gone"]), CancellationToken.None))
            .Should().Be(0);

        (await ConsumedAsync("conv-other")).Should().Be(300);
    }

    private async Task<int> ConsumedAsync(string key) => (await _tracker.GetStatusAsync(key)).ConsumedTokens;
}
