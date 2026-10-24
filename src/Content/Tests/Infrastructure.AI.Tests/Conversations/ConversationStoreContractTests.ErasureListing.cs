using Application.AI.Common.Interfaces.Compliance;
using FluentAssertions;
using Xunit;

namespace Infrastructure.AI.Tests.Conversations;

/// <summary>
/// The id-only listing erasure reads before deleting anything (#907), owed by every conversation store that can erase.
/// Listing another person's conversation would erase what it leads to; missing one of the person's would leave it behind.
/// </summary>
public abstract partial class ConversationStoreContractTests
{
    private IConversationIdSource IdSource => (IConversationIdSource)Store;

    [Fact]
    public async Task ListConversationIdsAsync_ListsEveryConversationOfTheOwner_AndNoOneElses()
    {
        var first = await Store.CreateAsync("agent", Owner);
        var second = await Store.CreateAsync("agent", Owner);
        await Store.AppendMessageAsync(second.Id, Owner, UserMessage("has a transcript"));
        await Store.CreateAsync("agent", Stranger);

        var listing = await IdSource.ListConversationIdsAsync(Owner, CancellationToken.None);

        listing.Ids.Should().BeEquivalentTo([first.Id, second.Id]);
        listing.Unreadable.Should().Be(0);
    }

    [Fact]
    public async Task ListConversationIdsAsync_MatchesTheOwnerExactlyAsIssued()
    {
        // Conversations store the id as issued; a differently-cased id is a different person here.
        await Store.CreateAsync("agent", "Owner-Mixed");

        (await IdSource.ListConversationIdsAsync("owner-mixed", CancellationToken.None)).Ids.Should().BeEmpty();
        (await IdSource.ListConversationIdsAsync("Owner-Mixed", CancellationToken.None)).Ids.Should().ContainSingle();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task ListConversationIdsAsync_ABlankOwner_IsRefused(string ownerId)
    {
        var act = () => IdSource.ListConversationIdsAsync(ownerId, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>("listing the conversations of nobody in particular has no safe reading");
    }
}
