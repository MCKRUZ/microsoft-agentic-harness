using Application.Common.Exceptions.ExceptionTypes;
using FluentAssertions;
using Xunit;

namespace Infrastructure.AI.Tests.Conversations;

/// <summary>
/// Reserved conversation-id shapes (#907), owed by every conversation store. A planned-task run records data under ids
/// containing ':' in stores that carry no owner; a person who could open a conversation with such an id would have that
/// run's records erased along with their own.
/// </summary>
public abstract partial class ConversationStoreContractTests
{
    [Theory]
    [InlineData("planrun:plan-1")]
    [InlineData("run-1:step-1")]
    [InlineData("conv:")]
    public async Task GetOrCreateAsync_ReservedShape_IsRefusedAndNothingIsCreated(string conversationId)
    {
        var act = () => Store.GetOrCreateAsync("agent", Owner, conversationId);

        await act.Should().ThrowAsync<ReservedConversationIdException>();
        (await Store.ListAsync(Owner)).Should().BeEmpty("the refusal must come before anything is written");
    }

    [Fact]
    public async Task CreateAsync_ReservedShape_IsRefusedAndNothingIsCreated()
    {
        var act = () => Store.CreateAsync("agent", Owner, "run-1:step-1");

        await act.Should().ThrowAsync<ReservedConversationIdException>();
        (await Store.ListAsync(Owner)).Should().BeEmpty();
    }

    [Fact]
    public async Task GetOrCreateAsync_IdWithoutTheSeparator_IsStillCreated()
    {
        // The rule is the separator alone: the other characters the entry points allow stay usable.
        var record = await Store.GetOrCreateAsync("agent", Owner, "voice_session-8f14e45f");

        record.Id.Should().Be("voice_session-8f14e45f");
    }
}
