using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Interfaces.Compliance;
using Application.AI.Common.Models.Conversations;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common.Compliance;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Presentation.Common.Tests.Composition;

/// <summary>
/// The erasure journal through the real composition (#907): a store after the conversations fails once, after the
/// conversations are already gone, and the next request still reaches what they led to, then clears the journal.
/// </summary>
/// <remarks>
/// Before the journal, the retry listed no conversations at all, so the failed store's rows were unreachable for good while
/// its line read <c>Erased</c> with nothing removed. The failing store is the one addition to the production wiring; the
/// coordinator, the conversation store and the journal are the real ones.
/// </remarks>
[Collection(SqliteConnectionPoolCollection.Name)]
public sealed class ErasureJournalEndToEndTests : IDisposable
{
    private const string Subject = "Journal-Subject-1";
    private readonly string _workingDir = Path.Combine(Path.GetTempPath(), $"erasure-journal-e2e-{Guid.NewGuid():N}");

    public ErasureJournalEndToEndTests() => Directory.CreateDirectory(_workingDir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_workingDir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a file still held open is removed with the run's temp folder.
        }
    }

    [Fact]
    public async Task AStoreAfterTheConversationsFailsOnce_TheRetryStillReachesWhatTheyLedTo_AndClearsTheJournal()
    {
        var failsOnce = new FailsOnceEraser();
        await using var provider = CompositionRootTestHost.BuildProvider(
            new Dictionary<string, string?>
            {
                ["AppConfig:AI:Conversations:DatabasePath"] = Path.Combine(_workingDir, "conversations.db"),
                ["AppConfig:AI:Schedules:DatabasePath"] = Path.Combine(_workingDir, "schedules.db"),
                ["AppConfig:AI:Planner:DatabasePath"] = Path.Combine(_workingDir, "planner.db"),
                ["AppConfig:AI:Erasure:JournalDatabasePath"] = Path.Combine(_workingDir, "erasure-journal.db"),
                ["AppConfig:AI:ContextManagement:ToolResultStorage:StoragePath"] = Path.Combine(_workingDir, "spills"),
                ["AppConfig:AI:Rag:VectorStore:Provider"] = "faiss",
            },
            (services, _) => services.AddSingleton<IPersonalDataEraser>(failsOnce));

        string conversationId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var conversations = scope.ServiceProvider.GetRequiredService<IConversationStore>();
            conversationId = (await conversations.CreateAsync("agent", Subject)).Id;
            await conversations.AppendMessageAsync(conversationId, Subject,
                new ConversationMessage(Guid.NewGuid(), MessageRole.User, "hello", DateTimeOffset.UtcNow));
        }

        var first = await EraseAsync(provider);

        first.Stores.Single(s => s.Store == FailsOnceEraser.Name).Status.Should().Be(StoreErasureStatus.Failed);
        await using (var scope = provider.CreateAsyncScope())
        {
            var conversations = scope.ServiceProvider.GetRequiredService<IConversationStore>();
            (await conversations.ListAsync(Subject))
                .Should().BeEmpty("the conversations go before the stage after them; that is what strands the failed store");

            // Security review: someone else opening a conversation under the erased id would have their records filed
            // where the retry erases. The journal holds the id until the subject's erasure finishes.
            var squat = () => conversations.GetOrCreateAsync("agent", "Someone-Else", conversationId);
            await squat.Should().ThrowAsync<Application.Common.Exceptions.ExceptionTypes.ConversationAccessDeniedException>();
        }

        var second = await EraseAsync(provider);

        failsOnce.Subjects.Last().AllConversationIds.Should().Contain(conversationId,
            "the conversation is gone, so only the journal still leads to its records");
        second.Stores.Single(s => s.Store == FailsOnceEraser.Name).Status.Should().Be(StoreErasureStatus.Erased);
        await using (var scope = provider.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<IErasureJournal>().ReadAsync(Subject, CancellationToken.None))
                .IsEmpty.Should().BeTrue("every store succeeded, so the request's rows are cleared");
        }
    }

    private static async Task<ErasureReceipt> EraseAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IPersonalDataErasure>().EraseOwnerAsync(Subject, CancellationToken.None);
    }

    /// <summary>A store erased after the conversations that fails the first time, like a ledger whose database was busy.</summary>
    private sealed class FailsOnceEraser : IPersonalDataEraser
    {
        public const string Name = "store that fails once";
        private int _calls;

        public List<ErasureSubject> Subjects { get; } = [];

        public string ErasureStoreName => Name;

        public ErasureStage ErasureStage => ErasureStage.AfterConversations;

        public ErasureReach ErasureReach => ErasureReach.NamedConversations;

        public Task<int> EraseSubjectAsync(ErasureSubject subject, CancellationToken cancellationToken)
        {
            Subjects.Add(subject);
            return Interlocked.Increment(ref _calls) == 1
                ? Task.FromException<int>(new IOException("database is locked"))
                : Task.FromResult(subject.AllConversationIds.Count);
        }
    }
}
