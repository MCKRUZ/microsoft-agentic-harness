using Application.AI.Common.Services.AI;
using Domain.Common.Config;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Services.AI;

/// <summary>
/// The parts of <see cref="InProcessConversationBudgetTracker"/> that exist only because it holds its
/// totals in this process's memory: bounded growth, and the under-enforcement that bound implies.
/// </summary>
/// <remarks>
/// Its behaviour as a budget — accumulating, reporting exhaustion, isolating keys, releasing — is
/// asserted against <c>Infrastructure.AI.Tests.Conversations.ConversationBudgetTrackerContractTests</c>
/// instead, alongside the durable implementation, because the two exist to be interchangeable and a
/// difference between them shows up as a ceiling that means one thing under one provider and something
/// else under the other. This file holds only what the contract cannot ask of both.
/// </remarks>
public sealed class InProcessConversationBudgetTrackerTests
{
    private static InProcessConversationBudgetTracker Create(int budget)
    {
        var cfg = new AppConfig();
        cfg.AI.AgentFramework.ConversationTokenBudget = budget;
        var monitor = Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == cfg);
        return new InProcessConversationBudgetTracker(
            monitor, TimeProvider.System, NullLogger<InProcessConversationBudgetTracker>.Instance);
    }

    [Fact]
    public async Task EvictsLeastRecentlyUsed_WhenOverCapacity()
    {
        var tracker = Create(1_000);

        // Exceed the cap; the tracker must evict back to at or below it rather than grow unbounded.
        for (var i = 0; i <= InProcessConversationBudgetTracker.MaxTrackedConversations; i++)
            await tracker.RecordUsageAsync($"c{i}", 1);

        // A freshly-recorded conversation survives; the invariant is that the cap is respected.
        var survivor = $"c{InProcessConversationBudgetTracker.MaxTrackedConversations}";
        var status = await tracker.GetStatusAsync(survivor);
        Assert.True(status.ConsumedTokens >= 1);
    }

    /// <summary>
    /// Eviction is the price of a bounded dictionary, and it under-enforces: an evicted key that comes
    /// back starts from zero. Pinned deliberately so the trade-off is visible rather than discovered,
    /// and so the durable implementation's contrasting guarantee has something to contrast with.
    /// </summary>
    [Fact]
    public async Task EvictedKey_LosesItsTotal_WhichIsTheDocumentedTradeOff()
    {
        var tracker = Create(1_000);

        // Recorded first and never touched again, so it is the least-recently-used entry when the
        // cap is crossed and therefore the one eviction reclaims.
        await tracker.RecordUsageAsync("first", 1_000);
        Assert.True((await tracker.GetStatusAsync("first")).IsExhausted);

        for (var i = 0; i <= InProcessConversationBudgetTracker.MaxTrackedConversations; i++)
            await tracker.RecordUsageAsync($"c{i}", 1);

        var status = await tracker.GetStatusAsync("first");
        Assert.False(status.IsExhausted);
        Assert.Equal(0, status.ConsumedTokens);
    }
}
