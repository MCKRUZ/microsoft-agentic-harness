using Application.AI.Common.Interfaces.Compliance;
using FluentAssertions;
using Xunit;

namespace Infrastructure.AI.Tests.Conversations;

/// <summary>
/// Finding idle conversations for retention (#901), owed by every conversation store. Getting "idle" wrong in one
/// direction deletes a conversation someone is using; in the other it keeps one forever.
/// </summary>
public abstract partial class ConversationStoreContractTests
{
    private IIdleConversationSource Idle => (IIdleConversationSource)Store;

    [Fact]
    public async Task ListIdleAsync_ReturnsOnlyConversationsLastWrittenBeforeTheCutoff_OldestFirst_EveryOwner()
    {
        var oldest = await Store.CreateAsync("agent", Owner);
        Clock.Advance(TimeSpan.FromDays(1));
        var older = await Store.CreateAsync("agent", Stranger);
        Clock.Advance(TimeSpan.FromDays(1));
        var cutoff = Clock.GetUtcNow();
        var recent = await Store.CreateAsync("agent", Owner);

        var idle = await Idle.ListIdleAsync(cutoff, 10, CancellationToken.None);

        idle.Select(c => (c.Id, c.OwnerId)).Should().Equal((oldest.Id, Owner), (older.Id, Stranger));
        idle.Select(c => c.Id).Should().NotContain(recent.Id, "written at the cutoff is not idle");
    }

    [Fact]
    public async Task ListIdleAsync_ANewMessage_MakesAnOldConversationActiveAgain()
    {
        var conversation = await Store.CreateAsync("agent", Owner);
        Clock.Advance(TimeSpan.FromDays(30));
        await Store.AppendMessageAsync(conversation.Id, Owner, UserMessage("still here"));
        Clock.Advance(TimeSpan.FromDays(1));

        var idle = await Idle.ListIdleAsync(Clock.GetUtcNow() - TimeSpan.FromDays(2), 10, CancellationToken.None);

        idle.Should().BeEmpty("the conversation was written to a day ago, after the cutoff");
    }

    [Fact]
    public async Task ListIdleAsync_ReadingAConversation_DoesNotMakeItActive()
    {
        var conversation = await Store.CreateAsync("agent", Owner);
        Clock.Advance(TimeSpan.FromDays(30));
        await Store.GetAsync(conversation.Id, Owner);

        var idle = await Idle.ListIdleAsync(Clock.GetUtcNow() - TimeSpan.FromDays(1), 10, CancellationToken.None);

        idle.Select(c => c.Id).Should().Equal(conversation.Id);
    }

    [Fact]
    public async Task ListIdleAsync_StopsAtTheLimit_KeepingTheOldest()
    {
        var first = await Store.CreateAsync("agent", Owner);
        Clock.Advance(TimeSpan.FromHours(1));
        await Store.CreateAsync("agent", Owner);
        Clock.Advance(TimeSpan.FromDays(1));

        var idle = await Idle.ListIdleAsync(Clock.GetUtcNow(), 1, CancellationToken.None);

        idle.Select(c => c.Id).Should().Equal(first.Id);
    }

    [Fact]
    public async Task DeleteIfIdleAsync_DeletesOnlyWhenStillTheOwnersAndStillIdle()
    {
        var conversation = await Store.CreateAsync("agent", Owner);
        await Store.AppendMessageAsync(conversation.Id, Owner, UserMessage("hello"));
        Clock.Advance(TimeSpan.FromDays(2));
        var cutoff = Clock.GetUtcNow() - TimeSpan.FromDays(1);

        (await Idle.DeleteIfIdleAsync(conversation.Id, Stranger, cutoff, CancellationToken.None))
            .Should().BeFalse("a conversation listed under one owner is never deleted for another");
        (await Idle.DeleteIfIdleAsync(conversation.Id, Owner, Clock.GetUtcNow() - TimeSpan.FromDays(3), CancellationToken.None))
            .Should().BeFalse("written to after the cutoff, so not idle");
        (await Store.GetAsync(conversation.Id, Owner)).Should().NotBeNull("both refusals must leave it standing");

        (await Idle.DeleteIfIdleAsync(conversation.Id, Owner, cutoff, CancellationToken.None)).Should().BeTrue();
        (await Store.GetAsync(conversation.Id, Owner)).Should().BeNull();
        (await Idle.DeleteIfIdleAsync(conversation.Id, Owner, cutoff, CancellationToken.None))
            .Should().BeFalse("nothing is left to delete");
    }

    [Fact]
    public async Task DeleteIfIdleAsync_AConversationStartedAgainUnderTheSameId_IsNotDeleted()
    {
        // The race the conditional delete exists for: the idle conversation was deleted and a new one created with its
        // id between the sweep's check and its delete.
        var original = await Store.CreateAsync("agent", Owner);
        Clock.Advance(TimeSpan.FromDays(2));
        var cutoff = Clock.GetUtcNow() - TimeSpan.FromDays(1);
        await Store.DeleteAsync(original.Id, Owner);
        await Store.CreateAsync("agent", Owner, original.Id);

        (await Idle.DeleteIfIdleAsync(original.Id, Owner, cutoff, CancellationToken.None)).Should().BeFalse();
        (await Store.GetAsync(original.Id, Owner)).Should().NotBeNull();
    }

    [Fact]
    public async Task IsIdleAsync_AnswersForTheCutoff_AndIsFalseForAConversationThatIsGone()
    {
        var conversation = await Store.CreateAsync("agent", Owner);
        Clock.Advance(TimeSpan.FromDays(2));
        var cutoff = Clock.GetUtcNow() - TimeSpan.FromDays(1);

        (await Idle.IsIdleAsync(conversation.Id, cutoff, CancellationToken.None)).Should().BeTrue();

        await Store.AppendMessageAsync(conversation.Id, Owner, UserMessage("back again"));
        (await Idle.IsIdleAsync(conversation.Id, cutoff, CancellationToken.None)).Should().BeFalse();

        await Store.DeleteAsync(conversation.Id, Owner);
        var everythingIsIdle = Clock.GetUtcNow() + TimeSpan.FromDays(1);
        (await Idle.IsIdleAsync(conversation.Id, everythingIsIdle, CancellationToken.None))
            .Should().BeFalse("a conversation that no longer exists is not idle; there is nothing to expire");
    }
}
