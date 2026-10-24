using Application.AI.Common.Interfaces.Compliance;
using Application.Common.Exceptions.ExceptionTypes;
using Domain.Common.Compliance;
using FluentAssertions;
using Xunit;

namespace Infrastructure.AI.Tests.Conversations;

/// <summary>
/// Conversation ids an unfinished erasure holds (#907), owed by every conversation store. Until the erased person's
/// retry finishes, the stores without an owner still file records under the erased conversation's id; a new conversation
/// another person opened under it would have its records erased by that retry.
/// </summary>
public abstract partial class ConversationStoreContractTests
{
    /// <summary>The journal every store under test is built with; fixtures pass it to their store.</summary>
    protected HoldingJournal ErasureJournal { get; } = new();

    [Fact]
    public async Task GetOrCreateAsync_AnIdAnotherPersonsErasureHolds_IsRefusedAsIfItWereTheirs()
    {
        ErasureJournal.Hold("erased-1", Stranger);

        var act = () => Store.GetOrCreateAsync("agent", Owner, "erased-1");

        await act.Should().ThrowAsync<ConversationAccessDeniedException>(
            "refused like someone else's conversation, so the refusal says nothing about the erasure");
        (await Store.ListAsync(Owner)).Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAsync_AnIdAnotherPersonsErasureHolds_IsRefused()
    {
        ErasureJournal.Hold("erased-1", Stranger);

        var act = () => Store.CreateAsync("agent", Owner, "erased-1");

        await act.Should().ThrowAsync<ConversationAccessDeniedException>();
    }

    [Fact]
    public async Task GetOrCreateAsync_AnIdThePersonsOwnErasureHolds_IsStillTheirsToUse()
    {
        // The records under it are theirs either way, so reusing it erases nothing of anyone else's.
        ErasureJournal.Hold("erased-1", Owner);

        (await Store.GetOrCreateAsync("agent", Owner, "erased-1")).Id.Should().Be("erased-1");
    }

    /// <summary>An erasure journal that holds exactly the ids a test gives it.</summary>
    protected sealed class HoldingJournal : IErasureJournal
    {
        private readonly List<(string ConversationId, string OwnerId)> _held = [];

        public void Hold(string conversationId, string ownerId) => _held.Add((conversationId, ownerId));

        public Task<bool> HoldsForAnotherOwnerAsync(string conversationId, string ownerId, CancellationToken cancellationToken) =>
            Task.FromResult(_held.Any(h => h.ConversationId == conversationId && h.OwnerId != ownerId));

        public Task<ErasureJournalEntries> ReadAsync(string ownerId, CancellationToken cancellationToken) =>
            Task.FromResult(ErasureJournalEntries.Empty);

        public Task RecordAsync(string ownerId, string requestId, ErasureJournalEntries entries, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task ClearAsync(string ownerId, string requestId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IDisposable> BeginRequestAsync(string ownerId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The conversation stores never take an erasure turn.");
    }
}
