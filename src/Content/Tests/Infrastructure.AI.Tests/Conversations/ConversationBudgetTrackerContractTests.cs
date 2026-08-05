using Application.AI.Common.Interfaces.AI;
using FluentAssertions;
using Xunit;

namespace Infrastructure.AI.Tests.Conversations;

/// <summary>
/// The behaviour every <see cref="IConversationBudgetTracker"/> implementation owes its callers,
/// asserted against each of them.
/// </summary>
/// <remarks>
/// Shared rather than duplicated because the two implementations exist to be interchangeable: a host
/// swaps one for the other by changing <c>AppConfig.AI.Conversations.Provider</c>, and a difference
/// between them shows up as a configured ceiling meaning one thing under one provider and something
/// else under the other. The same reasoning that put both stores behind
/// <see cref="ConversationStoreContractTests"/> and both leases behind
/// <see cref="ConversationTurnLeaseContractTests"/>.
/// </remarks>
public abstract class ConversationBudgetTrackerContractTests
{
    /// <summary>The ceiling every implementation under this contract is built with.</summary>
    protected const int Ceiling = 1_000;

    /// <summary>The implementation under test, configured with a <see cref="Ceiling"/>-token budget.</summary>
    protected abstract IConversationBudgetTracker Tracker { get; }

    /// <summary>
    /// A second implementation with no ceiling configured, so the opt-in behaviour can be asserted
    /// without the fixture rebuilding its own configuration.
    /// </summary>
    protected abstract IConversationBudgetTracker DisabledTracker { get; }

    /// <summary>A key nothing else in this test class uses.</summary>
    protected static string NewKey() => $"key-{Guid.NewGuid():N}";

    [Fact]
    public async Task GetStatusAsync_BudgetDisabled_ReportsDisabledAndNeverExhausted()
    {
        var key = NewKey();
        await DisabledTracker.RecordUsageAsync(key, 1_000_000);

        var status = await DisabledTracker.GetStatusAsync(key);

        status.IsEnabled.Should().BeFalse();
        status.IsExhausted.Should().BeFalse("a ceiling of zero means unbounded, not exhausted");
        status.ConsumedTokens.Should().Be(0, "a disabled tracker stores nothing");
    }

    [Fact]
    public async Task RecordUsageAsync_AccumulatesAcrossTurns_UnderOneKey()
    {
        var key = NewKey();

        await Tracker.RecordUsageAsync(key, 300);
        await Tracker.RecordUsageAsync(key, 250);

        var status = await Tracker.GetStatusAsync(key);
        status.IsEnabled.Should().BeTrue();
        status.TotalBudget.Should().Be(Ceiling);
        status.ConsumedTokens.Should().Be(550, "the whole point is that turns sum");
        status.RemainingBudget.Should().Be(450);
        status.IsExhausted.Should().BeFalse();
    }

    [Fact]
    public async Task GetStatusAsync_AtTheCeiling_ReportsExhausted()
    {
        var key = NewKey();

        await Tracker.RecordUsageAsync(key, 600);
        (await Tracker.GetStatusAsync(key)).IsExhausted.Should().BeFalse();

        await Tracker.RecordUsageAsync(key, 400); // reaches exactly the ceiling
        var status = await Tracker.GetStatusAsync(key);

        status.IsExhausted.Should().BeTrue("the boundary is inclusive — at the ceiling is spent");
        status.RemainingBudget.Should().Be(0);
    }

    [Fact]
    public async Task RecordUsageAsync_OvershootingTheCeiling_FloorsRemainingAtZero()
    {
        var key = NewKey();
        await Tracker.RecordUsageAsync(key, 1_500);

        var status = await Tracker.GetStatusAsync(key);
        status.IsExhausted.Should().BeTrue();
        status.ConsumedTokens.Should().Be(1_500, "the overshoot is reported, not clipped");
        status.RemainingBudget.Should().Be(0, "remaining is a floor, never negative");
    }

    [Fact]
    public async Task Keys_AreIsolatedFromOneAnother()
    {
        // Load-bearing: plan runs and conversations share this tracker, and a plan whose spend landed
        // on a conversation's ceiling would stop a conversation that had spent nothing.
        var spent = NewKey();
        var fresh = NewKey();

        await Tracker.RecordUsageAsync(spent, Ceiling);
        await Tracker.RecordUsageAsync(fresh, 100);

        (await Tracker.GetStatusAsync(spent)).IsExhausted.Should().BeTrue();
        (await Tracker.GetStatusAsync(fresh)).IsExhausted.Should().BeFalse();
        (await Tracker.GetStatusAsync(fresh)).RemainingBudget.Should().Be(900);
    }

    [Fact]
    public async Task ReleaseAsync_DropsTheKeysTotal()
    {
        var key = NewKey();
        await Tracker.RecordUsageAsync(key, Ceiling);
        (await Tracker.GetStatusAsync(key)).IsExhausted.Should().BeTrue();

        await Tracker.ReleaseAsync(key);

        var status = await Tracker.GetStatusAsync(key);
        status.IsExhausted.Should().BeFalse();
        status.ConsumedTokens.Should().Be(0);
    }

    [Fact]
    public async Task ReleaseAsync_UnknownKey_IsSilent()
    {
        // Callers release in a finally, including after a setup that never recorded anything.
        await Tracker.ReleaseAsync(NewKey());
    }

    [Fact]
    public async Task RecordUsageAsync_ZeroTokens_IsANoOp()
    {
        var key = NewKey();
        await Tracker.RecordUsageAsync(key, 0);

        (await Tracker.GetStatusAsync(key)).ConsumedTokens.Should().Be(0);
    }

    [Fact]
    public async Task RecordUsageAsync_NegativeTokens_Throws()
    {
        // Negative usage would let a caller refund itself past a ceiling it had already crossed.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => Tracker.RecordUsageAsync(NewKey(), -1));
    }

    [Fact]
    public async Task EmptyKey_Throws()
    {
        // An empty key is "some budget, unspecified": every caller passing one would share a ceiling.
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Tracker.GetStatusAsync(string.Empty));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Tracker.RecordUsageAsync(string.Empty, 1));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Tracker.ReleaseAsync(string.Empty));
    }
}
