using FluentAssertions;
using Infrastructure.AI.Governance;
using Xunit;

namespace Infrastructure.AI.Tests.Governance;

/// <summary>
/// Tests for <see cref="NullToolCallLedger"/>: every claim succeeds, even a repeat of the same
/// pair — the answer the durable ledger gives only while
/// <c>GovernanceDurableStateConfig.CallOnceEnforcementEnabled</c> is false.
/// </summary>
public sealed class NullToolCallLedgerTests
{
    [Fact]
    public async Task TryClaimAsync_RepeatedClaimOfTheSamePair_AlwaysSucceeds()
    {
        var ledger = new NullToolCallLedger();

        var first = await ledger.TryClaimAsync("conv-a", "start_diagnostic_session", CancellationToken.None);
        var second = await ledger.TryClaimAsync("conv-a", "start_diagnostic_session", CancellationToken.None);

        first.Should().BeTrue();
        second.Should().BeTrue();
    }

    [Theory]
    [InlineData("", "tool")]
    [InlineData("   ", "tool")]
    [InlineData("conv", "")]
    [InlineData("conv", "   ")]
    public async Task TryClaimAsync_BlankArgument_Throws(string conversationId, string toolName)
    {
        var ledger = new NullToolCallLedger();

        var act = async () => await ledger.TryClaimAsync(conversationId, toolName, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }
}
